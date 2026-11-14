// Проверяет реальные дочерние процессы: RPC, HTTP-готовность, авторизованный запрос, остановку, отмену и отказ на занятом порту
namespace OpenLLMCluster.Tests;

public sealed class EngineProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "open llm cluster engine " + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RpcProcessOpensPortAndDisposeClosesOnlyOwnedProcess()
    {
        var profile = CreateProfile(NodeRole.Worker);
        using var host = new EngineProcessHost(CreateRunner());
        await host.StartAsync(profile, "", TestContext.Current.CancellationToken);
        Assert.True(host.IsRunning);
        Assert.True((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.RpcPort, TestContext.Current.CancellationToken)).IsOpen);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.TestAsync("READY", TestContext.Current.CancellationToken));
        host.Dispose();
        Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.RpcPort, TestContext.Current.CancellationToken)).IsOpen);
    }

    [Fact]
    public async Task MainProcessRequiresReadyHealthAndAuthenticatedModelsBeforeTestRequest()
    {
        var profile = CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(CreateRunner());
        const string key = "test-api-key-secret";
        await host.StartAsync(profile, key, TestContext.Current.CancellationToken);
        Assert.True(host.IsRunning);
        var result = await host.TestAsync("Ответь READY", TestContext.Current.CancellationToken);
        Assert.Equal("READY", result.Text);
        Assert.Equal(1, result.CompletionTokens);
        Assert.DoesNotContain(key, host.DiagnosticOutput);
        host.Stop();
        Assert.False(host.IsRunning);
        Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.ApiPort, TestContext.Current.CancellationToken)).IsOpen);
    }

    [Fact]
    public async Task GatewayWaitsForBackendToBindBeforeDeclaringStartupFailure()
    {
        var profile = CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(CreateRunner("delayed-listen"));
        await host.StartAsync(profile, "delayed-backend-key", TestContext.Current.CancellationToken);
        Assert.True(host.IsRunning);
        Assert.Contains("fixture waiting before HTTP bind", host.DiagnosticOutput);
        Assert.Equal("READY", (await host.TestAsync("READY", TestContext.Current.CancellationToken)).Text);
        host.Stop();
        Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.ApiPort, TestContext.Current.CancellationToken)).IsOpen);
    }

    [Fact]
    public async Task MainProcessEnumeratesRpcBeforeListDevicesAndThenStarts()
    {
        var worker = CreateProfile(NodeRole.Worker);
        using var rpc = new EngineProcessHost(CreateRunner());
        using var server = new EngineProcessHost(CreateRunner());
        await rpc.StartAsync(worker, "", TestContext.Current.CancellationToken);
        var main = CreateProfile(NodeRole.Coordinator) with
        {
            Nodes = [new() { Name = "fixture-worker", Host = "127.0.0.1", RpcPort = worker.RpcPort, AgentPort = worker.ApiPort }]
        };
        await server.StartAsync(main, "secret", TestContext.Current.CancellationToken);
        Assert.True(server.IsRunning);
        Assert.Equal("READY", (await server.TestAsync("READY", TestContext.Current.CancellationToken)).Text);
    }

    [Fact]
    public async Task RpcEndpointInConnectionErrorCannotConfirmAvailableDevice()
    {
        var profile = CreateProfile(NodeRole.Coordinator);
        profile = profile with { Nodes = [new() { Name = "fixture-worker", Host = "127.0.0.1", RpcPort = profile.RpcPort, AgentPort = profile.ApiPort }] };
        var original = CreateRunner("rpc-unavailable");
        var modelStarted = false;
        var runner = new EngineCommandRunner(plan =>
        {
            if (plan.Arguments.Contains("--model")) modelStarted = true;
            return original.StartFactory(plan);
        });
        using var host = new EngineProcessHost(runner);
        var error = await Record.ExceptionAsync(() => host.StartAsync(profile, "rpc-error-key", TestContext.Current.CancellationToken));
        Assert.False(modelStarted, "A failed RPC enumeration must prevent model startup");
        Assert.Contains("RPC не подтвердил устройство узла fixture-worker", Assert.IsType<InvalidOperationException>(error).Message);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task LoadingModelIsNotReportedReadyAndCancellationStopsProcess()
    {
        var profile = CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(CreateRunner("loading"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(profile, "secret", cancellation.Token));
        Assert.False(host.IsRunning);
        Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.ApiPort, TestContext.Current.CancellationToken)).IsOpen);
    }

    [Fact]
    public async Task StartupFailureContainsLogWithoutApiKey()
    {
        using var host = new EngineProcessHost(CreateRunner("exit"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CreateProfile(NodeRole.Coordinator),
            "do-not-log-this", TestContext.Current.CancellationToken));
        Assert.Contains("fixture startup failed", error.Message);
        Assert.DoesNotContain("do-not-log-this", error.Message);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task OccupiedPortIsNotAdoptedOrStopped()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var profile = CreateProfile(NodeRole.Worker) with { RpcPort = port };
        using var host = new EngineProcessHost(CreateRunner());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(profile, "", TestContext.Current.CancellationToken));
        Assert.Contains("занят", error.Message);
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        Assert.True(connection.Connected);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task SelectedReleaseMustMatchExecutableBuildBeforeProcessStarts()
    {
        var profile = CreateProfile(NodeRole.Worker) with { LlamaReleaseTag = "b99999" };
        using var host = new EngineProcessHost(CreateRunner());
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => host.StartAsync(profile, "", TestContext.Current.CancellationToken));
        Assert.Contains("b99999", error.Message);
        Assert.False(host.IsRunning);
    }

    /// <summary>Создаёт файлы-маркеры для запуска отдельного Fixture-проекта</summary>
    public RouterProfile CreateProfile(NodeRole role)
    {
        Directory.CreateDirectory(_root);
        foreach (var item in new[] { NodeRole.Worker, NodeRole.Coordinator })
            File.WriteAllText(Path.Combine(_root, RuntimePlanner.GetExecutableName(item)), "fixture marker");
        var model = Path.Combine(_root, "model fixture.gguf");
        File.WriteAllBytes(model, [71, 71, 85, 70, 3, 0, 0, 0]);
        return new() { Role = role, RuntimeDirectory = _root, ModelPath = model, ApiPort = FreePort(), RpcPort = FreePort(), StartupTimeoutSeconds = 15 };
    }

    /// <summary>Запускает изолированный Fixture вместо бинарного файла llama.cpp</summary>
    public static EngineCommandRunner CreateRunner(string mode = "ready") => new(plan => CreateStart(plan, mode));

    // Передаёт настоящие аргументы и окружение в отдельный процесс без оболочки
    private static ProcessStartInfo CreateStart(LaunchPlan plan, string mode)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Open-LLM-Cluster.slnx"))) root = root.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var fixture = Path.Combine(root!.FullName, "tests", "OpenLLMCluster.EngineFixture", "bin", configuration, "net10.0", "OpenLLMCluster.EngineFixture.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(plan.Executable)! };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add("--fixture-role");
        start.ArgumentList.Add(Path.GetFileName(plan.Executable).StartsWith("ggml-rpc", StringComparison.Ordinal) ? "rpc" : "main");
        foreach (var argument in plan.Arguments) start.ArgumentList.Add(argument);
        start.Environment["OPEN_LLM_CLUSTER_FIXTURE_MODE"] = mode;
        return start;
    }

    // Получает временный порт для собственного процесса
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Удаляет файлы-маркеры теста</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
