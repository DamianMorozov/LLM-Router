namespace OpenLLMCluster.Agent;

/// <summary>Передаёт inference-запросы локальному движку и нормализует Effort по возможностям загруженного шаблона</summary>
public sealed class InferenceGateway(Uri backend, string token) : IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private ModelCapabilities? _capabilities;
    private static readonly string[] _hopHeaders = ["Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Host", "Content-Length"];

    /// <summary>Создаёт отдельный HTTP-процесс из пакета агента без запуска диагностических служб</summary>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        var address = IPAddress.Parse(builder.Configuration["Gateway:Address"] ?? "127.0.0.1");
        var port = builder.Configuration.GetValue<int>("Gateway:Port");
        var backend = new Uri(builder.Configuration["Gateway:Backend"] ?? throw new ArgumentException("Не указан локальный движок"));
        if (!backend.IsLoopback || backend.Scheme != "http") throw new ArgumentException("Движок шлюза должен слушать локальный HTTP");
        var token = builder.Configuration["Gateway:Token"] ?? "";
        if (token.Length == 0) throw new ArgumentException("Не указан ключ inference API");
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port, listen => listen.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        var gateway = new InferenceGateway(backend, token);
        app.Lifetime.ApplicationStopped.Register(gateway.Dispose);
        app.Run(gateway.ForwardAsync);
        return app;
    }

    // Проверяет ключ до чтения тела, сохраняя поток ответа и исходные HTTP-коды движка
    private async Task ForwardAsync(HttpContext context)
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes("Bearer " + token),
            Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString())))
        { context.Response.StatusCode = 401; return; }
        if (context.Request.Path == "/olc/gateway-ready") { context.Response.StatusCode = 204; return; }
        try
        {
            byte[]? body = null;
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
                body = buffer.ToArray();
            }
            var generation = context.Request.Method == "POST" && context.Request.Path.Value is "/v1/chat/completions" or "/v1/responses";
            JsonObject? json = null;
            var hadEffort = false;
            if (generation && body is not null)
            {
                try { json = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { }
                if (json is not null)
                {
                    hadEffort = HasEffort(json);
                    var caps = _capabilities ?? await new EngineApiClient(_client).ReadCapabilitiesAsync(backend, token, context.RequestAborted);
                    if (caps.SupportsEffort is not null) _capabilities = caps;
                    var fallback = Normalize(json, caps);
                    Console.WriteLine(fallback ? "OLC_EFFORT_FALLBACK" : HasEffort(json) ? "OLC_EFFORT_CLIENT" : "OLC_EFFORT_DEFAULT");
                    body = Encoding.UTF8.GetBytes(json.ToJsonString());
                }
            }
            using var request = CreateRequest(context, body);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            if (generation && hadEffort && json is not null && response.StatusCode == HttpStatusCode.BadRequest)
            {
                var error = await response.Content.ReadAsStringAsync(context.RequestAborted);
                if (error.Contains("reasoning", StringComparison.OrdinalIgnoreCase) && error.Contains("effort", StringComparison.OrdinalIgnoreCase)
                    && (error.Contains("invalid", StringComparison.OrdinalIgnoreCase) || error.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
                    && HasEffort(json))
                {
                    RemoveEffort(json);
                    Console.WriteLine("OLC_EFFORT_FALLBACK");
                    using var retry = CreateRequest(context, Encoding.UTF8.GetBytes(json.ToJsonString()));
                    using var retried = await _client.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                    await CopyResponseAsync(context, retried);
                    return;
                }
            }
            await CopyResponseAsync(context, response);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Inference gateway: " + ex.GetType().Name);
            if (!context.Response.HasStarted) context.Response.StatusCode = 502;
            else context.Abort();
        }
    }

    // При подтверждённом списке проверяет точное значение; неизвестный уровень движка проверяется его HTTP-ответом
    private static bool Normalize(JsonObject body, ModelCapabilities caps)
    {
        var present = HasEffort(body);
        if (!present) return false;
        var node = body["reasoning_effort"] ?? (body["reasoning"] as JsonObject)?["effort"]
            ?? (body["chat_template_kwargs"] as JsonObject)?["reasoning_effort"];
        var value = node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
        var nested = (body["reasoning"] as JsonObject)?.ContainsKey("effort") == true && !body.ContainsKey("reasoning_effort");
        var valid = value.Length > 0 && value != "default" && caps.SupportsEffort != false
            && (caps.EffortLevels.Length > 0 ? caps.EffortLevels.Contains(value, StringComparer.Ordinal) : ModelSettings.EffortLevels.Contains(value));
        RemoveEffort(body);
        if (valid)
        {
            if (nested) body["reasoning"]!["effort"] = value;
            else body["reasoning_effort"] = value;
        }
        return !valid && value != "default";
    }

    // Учитывает Chat Completions, Responses и прямой параметр шаблона без изменения остальных полей
    private static bool HasEffort(JsonObject body) => body.ContainsKey("reasoning_effort")
        || (body["reasoning"] as JsonObject)?.ContainsKey("effort") == true
        || (body["chat_template_kwargs"] as JsonObject)?.ContainsKey("reasoning_effort") == true;

    // Отсутствующий параметр оставляет решение шаблону вместо отправки строки default
    private static void RemoveEffort(JsonObject body)
    {
        body.Remove("reasoning_effort");
        (body["reasoning"] as JsonObject)?.Remove("effort");
        (body["chat_template_kwargs"] as JsonObject)?.Remove("reasoning_effort");
    }

    // Передаёт запрос только фиксированному локальному backend, исключая заголовки соединения
    private HttpRequestMessage CreateRequest(HttpContext context, byte[]? body)
    {
        var path = context.Request.Path.Value!.TrimStart('/') + context.Request.QueryString.Value;
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri(backend, path));
        if (body is not null) request.Content = new ByteArrayContent(body);
        foreach (var header in context.Request.Headers)
        {
            if (_hopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        return request;
    }

    // Сбрасывает каждую полученную порцию SSE, не дожидаясь завершения генерации
    private static async Task CopyResponseAsync(HttpContext context, HttpResponseMessage response)
    {
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            if (!_hopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) context.Response.Headers[header.Key] = header.Value.ToArray();
        await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    /// <summary>Освобождает HTTP-соединения при остановке процесса</summary>
    public void Dispose() => _client.Dispose();
}
