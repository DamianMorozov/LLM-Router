namespace OpenLLMCluster.Infrastructure;

/// <summary>Результат настоящего контрольного запроса к inference API</summary>
public sealed record EngineTestResult(string Text, int CompletionTokens, long ElapsedMilliseconds)
{
    public int? PromptTokens { get; init; }
    public int? CachedTokens { get; init; }
    public double? PromptMilliseconds { get; init; }
    public double? GenerationMilliseconds { get; init; }
    public double? PromptTokensPerSecond { get; init; }
    public double? GenerationTokensPerSecond { get; init; }
}

/// <summary>Поддержка Effort из фактического chat template запущенной модели</summary>
public sealed record ModelCapabilities(bool? SupportsEffort, string[] EffortLevels);

/// <summary>Проверяет готовность и OpenAI-совместимый запрос без вывода ключа API</summary>
public sealed partial class EngineApiClient(HttpClient? client = null)
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly HttpClient _client = client ?? _http;

    /// <summary>Читает возможности модели; старый сервер без /props оставляет поддержку неизвестной</summary>
    public async Task<ModelCapabilities> ReadCapabilitiesAsync(Uri baseUri, string apiKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var request = Request(HttpMethod.Get, new Uri(baseUri, "props"), apiKey);
            using var response = await _client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(null, []);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!document.RootElement.TryGetProperty("chat_template_caps", out var caps)
                || !caps.TryGetProperty("supports_reasoning_effort", out var supported)) return new(null, []);
            var levels = caps.TryGetProperty("reasoning_effort_levels", out var values)
                ? values.EnumerateArray().Select(value => value.GetString() ?? "").Where(value => value.Length > 0).ToArray() : [];
            return new(supported.GetBoolean(), levels);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Trace.TraceWarning("Возможности модели: {0}", ex.GetType().Name); return new(null, []); }
    }

    /// <summary>Отличает загрузку модели от успешного /health и доступности алиаса в /v1/models</summary>
    public async Task<bool> IsReadyAsync(Uri baseUri, string apiKey, CancellationToken ct = default)
    {
        try
        {
            using var request = Request(HttpMethod.Get, new Uri(baseUri, "health"), apiKey);
            using var response = await _client.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable) return false;
            response.EnsureSuccessStatusCode();
            using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!health.RootElement.TryGetProperty("status", out var status) || status.GetString() != "ok") return false;
            using var modelsRequest = Request(HttpMethod.Get, new Uri(baseUri, "v1/models"), apiKey);
            using var modelsResponse = await _client.SendAsync(modelsRequest, ct);
            modelsResponse.EnsureSuccessStatusCode();
            using var models = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync(ct));
            return models.RootElement.GetProperty("data").EnumerateArray().Any(item => item.GetProperty("id").GetString() == InferenceDefaults.ModelId);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null) { return false; }
        catch (Exception ex) { Trace.TraceError("Проверка inference API: {0}", ex.GetType().Name); throw; }
    }

    /// <summary>Посылает небольшой запрос и проверяет наличие сгенерированного текста</summary>
    public async Task<EngineTestResult> TestAsync(Uri baseUri, string apiKey, string prompt, CancellationToken ct = default)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var request = Request(HttpMethod.Post, new Uri(baseUri, "v1/chat/completions"), apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new { model = InferenceDefaults.ModelId, messages = new[] { new { role = "user", content = prompt } },
                max_tokens = 32, temperature = 0, stream = false }), Encoding.UTF8, "application/json");
            using var response = await _client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                if (apiKey.Length > 0) detail = detail.Replace(apiKey, "[key]", StringComparison.Ordinal);
                throw new InvalidOperationException("Контрольный запрос завершился HTTP " + (int)response.StatusCode + ": "
                    + detail);
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
            var text = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null;
            if (string.IsNullOrWhiteSpace(text) && message.TryGetProperty("reasoning_content", out var reasoning)) text = reasoning.GetString();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Модель вернула пустой текст контрольного ответа");
            var tokens = document.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("completion_tokens", out var count)
                ? count.GetInt32() : 0;
            var root = document.RootElement;
            var hasTimings = root.TryGetProperty("timings", out var timings);
            var hasUsage = root.TryGetProperty("usage", out usage);
            var hasDetails = hasUsage && usage.TryGetProperty("prompt_tokens_details", out _);
            return new(text, tokens, stopwatch.ElapsedMilliseconds)
            {
                PromptTokens = hasUsage && usage.TryGetProperty("prompt_tokens", out var promptTokens) ? promptTokens.GetInt32() : null,
                CachedTokens = hasDetails && usage.GetProperty("prompt_tokens_details").TryGetProperty("cached_tokens", out var cached)
                    ? cached.GetInt32() : null,
                PromptMilliseconds = hasTimings ? Number(timings, "prompt_ms") : null,
                GenerationMilliseconds = hasTimings ? Number(timings, "predicted_ms") : null,
                PromptTokensPerSecond = hasTimings ? Number(timings, "prompt_per_second") : null,
                GenerationTokensPerSecond = hasTimings ? Number(timings, "predicted_per_second") : null
            };
        }
        catch (Exception ex) { Trace.TraceError("Контрольный запрос: {0}", ex.GetType().Name); throw; }
    }

    // Сохраняет отсутствие измерения, не выдавая общее время запроса за время генерации
    private static double? Number(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 ? number : null;

    // Ключ передаётся только в заголовке, а не в URL или аргументах процесса
    private static HttpRequestMessage Request(HttpMethod method, Uri uri, string apiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }
}
