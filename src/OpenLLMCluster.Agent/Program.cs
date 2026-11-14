namespace OpenLLMCluster.Agent;

/// <summary>Запускает диагностический агент HTTP/2 с явным разрешением доступа из локальной сети</summary>
public static class Program
{
    /// <summary>Запускает службу до остановки процесса</summary>
    public static async Task Main(string[] args)
    {
        var app = args.Contains("--inference-gateway") ? InferenceGateway.Build(args.Where(value => value != "--inference-gateway").ToArray()) : Build(args);
        await app.RunAsync();
    }

    /// <summary>Создаёт одинаковый сервер для обычного запуска и интеграционных проверок</summary>
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var addressText = builder.Configuration["Router:ListenAddress"] ?? "127.0.0.1";
        if (!IPAddress.TryParse(addressText, out var address)) throw new ArgumentException("Не указан корректный IP-адрес агента");
        var port = builder.Configuration.GetValue("Router:Port", 5100);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Недопустимый порт агента");
        var token = builder.Configuration["Router:AccessToken"] ?? "";
        if (!IPAddress.IsLoopback(address) && (!builder.Configuration.GetValue("Router:AllowInsecureLan", false) || token.Length < 24))
            throw new InvalidOperationException("Для доступа из сети нужны Router:AllowInsecureLan и Router:AccessToken длиной не менее 24 символов");
        var tls = builder.Configuration.GetValue("Router:UseTls", false);
        var remote = builder.Configuration.GetValue("Router:AllowRemoteControl", false);
        if (remote && (!tls || token.Length < 24)) throw new InvalidOperationException("Удалённое управление требует TLS и ключ от 24 символов");
        var certificate = tls ? new AgentCertificateStore(builder.Configuration["Router:CertificatePath"]).GetOrCreate() : null;
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            if (certificate is not null) listen.UseHttps(certificate);
        }));
        builder.Services.AddSingleton<EnvironmentProbe>();
        builder.Services.AddSingleton(new NodeControlPolicy(remote));
        builder.Services.AddSingleton(new LocalModelPolicy(builder.Configuration["Router:LocalModelPath"] ?? ""));
        builder.Services.AddSingleton<RpcModelCache>();
        builder.Services.AddSingleton<LocalModelManager>();
        builder.Services.AddSingleton<LlamaInstallationStore>();
        builder.Services.AddSingleton<EngineProcessHost>();
        builder.Services.AddSingleton<RpcController>();
        configureServices?.Invoke(builder.Services);
        builder.Services.AddMagicOnion();
        var app = builder.Build();
        if (certificate is not null) app.Lifetime.ApplicationStopped.Register(certificate.Dispose);
        app.Use(async (context, next) =>
        {
            if (token.Length > 0 && !TokensEqual(token, context.Request.Headers["x-router-token"].ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        app.MapMagicOnionService();
        return app;
    }

    // Сравнивает ключи без вывода значений в журнал
    private static bool TokensEqual(string expected, string actual) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
}
