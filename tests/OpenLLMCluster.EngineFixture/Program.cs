namespace OpenLLMCluster.EngineFixture;

/// <summary>Дочерний процесс для проверки управления движком; не содержит LLM и не включается в публикацию</summary>
public static class Program
{
    /// <summary>Эмулирует только CLI, TCP и HTTP-контракт для автономных проверок</summary>
    public static async Task Main(string[] args)
    {
        if (args.Contains("--help")) { Console.WriteLine("--rpc --host --cache --list-devices"); return; }
        if (args.Contains("--version")) { Console.WriteLine("version: fixture (build 12318)"); return; }
        if (args.Contains("--list-devices"))
        {
            if (Environment.GetEnvironmentVariable("OPEN_LLM_CLUSTER_FIXTURE_MODE") == "rpc-unavailable")
            {
                Console.WriteLine("Available devices:\n  CUDA0: Fixture GPU (8192 MiB, 7168 MiB free)");
                Console.Error.WriteLine("Failed to connect to " + Value(args, "--rpc", ""));
                return;
            }
            var rpcIndex = Array.IndexOf(args, "--rpc");
            if (rpcIndex >= 0 && rpcIndex < Array.IndexOf(args, "--list-devices"))
                foreach (var endpoint in Value(args, "--rpc", "").Split(','))
                {
                    var addressUri = new Uri("tcp://" + endpoint);
                    using var client = new TcpClient();
                    await client.ConnectAsync(addressUri.Host, addressUri.Port);
                    Console.WriteLine("RPC0: " + endpoint + " (8192 MiB, 7168 MiB free)");
                }
            else Console.WriteLine("CPU: fixture");
            return;
        }
        var port = int.Parse(Value(args, "--port", "50052"));
        var address = IPAddress.Parse(Value(args, "--host", "127.0.0.1"));
        var mode = Environment.GetEnvironmentVariable("OPEN_LLM_CLUSTER_FIXTURE_MODE") ?? "ready";
        if (mode == "exit") { Console.Error.WriteLine("fixture startup failed " + Environment.GetEnvironmentVariable("LLAMA_API_KEY")); Environment.Exit(9); }
        if (Value(args, "--fixture-role", "main") == "rpc")
        {
            using var listener = new TcpListener(address, port);
            listener.Start();
            Console.WriteLine("fixture RPC ready");
            while (true) { using var connection = await listener.AcceptTcpClientAsync(); }
        }
        if (mode == "delayed-listen")
        {
            Console.WriteLine("fixture waiting before HTTP bind");
            await Task.Delay(3000);
        }
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(new UriBuilder("http", address.ToString(), port).Uri.ToString());
        var app = builder.Build();
        var progress = mode is "progress" or "progress-delayed-listen";
        var loading = mode == "loading" || progress;
        if (progress) Console.WriteLine("srv load_model: loading model 'fixture.gguf'");
        app.MapPost("/fixture/load-stage", (HttpContext context) =>
        {
            if (!progress || !Authorized(context)) return Results.Unauthorized();
            var stage = context.Request.Query["stage"].ToString();
            if (stage == "context") Console.WriteLine("srv load_model: initializing, n_slots = 1, n_ctx_slot = 8192");
            if (stage == "warmup") Console.WriteLine("common_init_from_params: warming up the model with an empty run");
            if (stage == "ready") loading = false;
            return Results.Ok();
        });
        app.MapGet("/health", () => loading ? Results.Json(new { status = "loading model" }, statusCode: 503)
            : Results.Json(new { status = "ok" }));
        app.MapGet("/v1/models", (HttpContext context) => Authorized(context)
            ? Results.Json(new { data = new[] { new { id = "open-llm-cluster" } } }) : Results.Unauthorized());
        app.MapGet("/props", (HttpContext context) => Authorized(context)
            ? mode == "effort-reject" ? Results.NotFound()
                : Results.Json(new { chat_template_caps = new { supports_reasoning_effort = mode.StartsWith("effort", StringComparison.Ordinal),
                    reasoning_effort_levels = mode.StartsWith("effort", StringComparison.Ordinal) ? new[] { "low", "high" } : [] } })
            : Results.Unauthorized());
        app.MapPost("/v1/chat/completions", (Func<HttpContext, Task<IResult>>)CompleteAsync);
        app.MapPost("/v1/responses", (Func<HttpContext, Task<IResult>>)CompleteAsync);
        if (mode == "progress-delayed-listen") await Task.Delay(2000);
        await app.RunAsync();
    }

    // Проверяет настоящий заголовок запроса из родительского процесса
    private static bool Authorized(HttpContext context)
        => context.Request.Headers.Authorization == "Bearer " + Environment.GetEnvironmentVariable("LLAMA_API_KEY");

    // Подтверждает структуру контрольного запроса без имитации качества модели
    private static async Task<IResult> CompleteAsync(HttpContext context)
    {
        if (!Authorized(context)) return Results.Unauthorized();
        using var body = await JsonDocument.ParseAsync(context.Request.Body);
        if (body.RootElement.GetProperty("model").GetString() != "open-llm-cluster"
            || (body.RootElement.TryGetProperty("max_tokens", out var limit) && limit.GetInt32() > 32))
            return Results.BadRequest();
        Console.WriteLine("CHAT_REQUEST_OK");
        var effort = body.RootElement.TryGetProperty("reasoning_effort", out var value) ? value.GetString()
            : body.RootElement.TryGetProperty("reasoning", out var reasoning) && reasoning.TryGetProperty("effort", out value) ? value.GetString() : "default";
        var mode = Environment.GetEnvironmentVariable("OPEN_LLM_CLUSTER_FIXTURE_MODE");
        if (mode == "effort-reject" && effort != "default") return Results.BadRequest(new { error = "unsupported reasoning_effort" });
        if (mode == "effort-context") return Results.BadRequest(new { error = "request exceeds the context window" });
        if (body.RootElement.TryGetProperty("stream", out var streaming) && streaming.GetBoolean())
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { observed_effort = effort }) + "\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(200, context.RequestAborted);
            await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
            return Results.Empty;
        }
        return Results.Json(new { choices = new[] { new { message = new { content = "READY" } } }, usage = new { completion_tokens = 1 }, observed_effort = effort });
    }

    // Читает значение отдельного аргумента, сохраняя пробелы внутри путей
    private static string Value(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
}
