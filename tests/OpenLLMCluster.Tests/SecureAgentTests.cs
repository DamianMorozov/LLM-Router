// Проверяет настоящий TLS/MagicOnion, закрепление сертификата, авторизацию и полный жизненный цикл RPC через агент
namespace OpenLLMCluster.Tests;

public sealed class SecureAgentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "open-llm-cluster-tls-" + Guid.NewGuid().ToString("N"));
    private readonly string _key = LocalAgentHost.GenerateToken();

    [Fact]
    public void ReloadedIdentityPreservesCertificateAndUsablePrivateKey()
    {
        var identity = new AgentCertificateStore(Path.Combine(_root, "reload-identity.dat"));
        byte[] originalCertificate;
        using (var created = identity.GetOrCreate()) originalCertificate = created.RawData;
        var protectedIdentity = File.ReadAllBytes(identity.FilePath);
        using var reloaded = new AgentCertificateStore(identity.FilePath).GetOrCreate();
        Assert.True(reloaded.HasPrivateKey);
        Assert.Equal(originalCertificate, reloaded.RawData);
        Assert.Equal(protectedIdentity, File.ReadAllBytes(identity.FilePath));
        using var privateKey = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPrivateKey(reloaded);
        using var publicKey = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPublicKey(reloaded);
        Assert.NotNull(privateKey);
        Assert.NotNull(publicKey);
        var challenge = RandomNumberGenerator.GetBytes(32);
        var signature = privateKey.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(publicKey.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        challenge[0] ^= 1;
        Assert.False(publicKey.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task PinnedTlsAcceptsOnlyMatchingIdentityAndKey()
    {
        var identity = new AgentCertificateStore(Path.Combine(_root, "identity.dat"));
        var pin = identity.Fingerprint();
        Assert.Equal(pin, new AgentCertificateStore(identity.FilePath).Fingerprint());
        await using var app = Agent.Program.Build(Arguments(identity));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var node = Endpoint(app.Services, pin);
        var client = new NodeAgentClient();
        var snapshot = await client.ProbeAsync(node, _key, TestContext.Current.CancellationToken);
        Assert.Equal(Environment.MachineName, snapshot.HostName);
        var wrongKey = await Assert.ThrowsAsync<RpcException>(() => client.ProbeAsync(node, "wrong", TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.Unauthenticated, wrongKey.StatusCode);
        await Assert.ThrowsAsync<RpcException>(() => client.ProbeAsync(node with { CertificateFingerprint = new string('0', 64) },
            _key, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ProbeAsync(node with { CertificateFingerprint = "" },
            _key, TestContext.Current.CancellationToken));
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RemoteControlStartsLogsAndStopsOnlyAgentOwnedRpc()
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Worker);
        var store = new LlamaInstallationStore(Path.Combine(_root, "runtime"));
        store.Save(new(profile.LlamaReleaseTag, LlamaBackend.Cpu, "fixture", profile.RuntimeDirectory, "fixture build 12318"));
        var identity = new AgentCertificateStore(Path.Combine(_root, "identity.dat"));
        await using var app = Agent.Program.Build(Arguments(identity, remote: true), services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(new EngineProcessHost(EngineProcessTests.CreateRunner()));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var node = Endpoint(app.Services, identity.Fingerprint()) with { RpcPort = profile.RpcPort };
        var client = new NodeAgentClient();
        var started = await client.StartRpcAsync(node, _key, new() { ListenAddress = "127.0.0.1", Port = profile.RpcPort },
            TestContext.Current.CancellationToken);
        Assert.True(started.IsRunning);
        Assert.Equal(profile.RpcPort, started.Port);
        Assert.Equal(profile.LlamaReleaseTag, started.ReleaseTag);
        Assert.True((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.RpcPort, TestContext.Current.CancellationToken)).IsOpen);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var status = await client.ReadRpcAsync(node, _key, TestContext.Current.CancellationToken);
        Assert.Contains("fixture RPC ready", status.Log);
        Assert.DoesNotContain(_key, status.Log);
        var stopped = await client.StopRpcAsync(node, _key, TestContext.Current.CancellationToken);
        Assert.False(stopped.IsRunning);
        Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.RpcPort, TestContext.Current.CancellationToken)).IsOpen);
        await Assert.ThrowsAsync<RpcException>(() => client.StartRpcAsync(node, _key,
            new() { ListenAddress = "not-an-address", Port = 0 }, TestContext.Current.CancellationToken));
        using var occupied = new TcpListener(IPAddress.Loopback, profile.RpcPort);
        occupied.Start();
        var error = await Assert.ThrowsAsync<RpcException>(() => client.StartRpcAsync(node, _key,
            new() { ListenAddress = "127.0.0.1", Port = profile.RpcPort }, TestContext.Current.CancellationToken));
        Assert.Contains("занят", error.Status.Detail);
        await client.StopRpcAsync(node, _key, TestContext.Current.CancellationToken);
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, profile.RpcPort, TestContext.Current.CancellationToken);
        Assert.True(connection.Connected);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TlsDoesNotImplicitlyEnableRemoteControl()
    {
        var identity = new AgentCertificateStore(Path.Combine(_root, "identity.dat"));
        await using var app = Agent.Program.Build(Arguments(identity));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<RpcException>(() => new NodeAgentClient().ReadRpcAsync(Endpoint(app.Services, identity.Fingerprint()),
            _key, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
        var denied = await Assert.ThrowsAsync<RpcException>(() => new NodeAgentClient().PrepareLocalModelAsync(
            Endpoint(app.Services, identity.Fingerprint()), _key, new(), TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PlaintextDiagnosticAgentRejectsControlEvenWhenClientBypassesUi()
    {
        await using var app = Agent.Program.Build(["--Router:Port", "0", "--Router:AccessToken", _key]);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var node = Endpoint(app.Services, "");
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(new UriBuilder("http", node.Host, node.AgentPort).Uri);
        var client = MagicOnion.Client.MagicOnionClient.Create<INodeControl>(channel)
            .WithHeaders(new Metadata { { "x-router-token", _key } }).WithCancellationToken(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<RpcException>(() => client.GetRpcStatusAsync().ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [AvaloniaFact]
    public async Task NodeButtonsVerifyPairingAndReadRemoteStatusThroughTls()
    {
        var identity = new AgentCertificateStore(Path.Combine(_root, "ui-identity.dat"));
        await using var app = Agent.Program.Build(Arguments(identity, remote: true));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var vm = new MainWindowViewModel(new ProfileStore(Path.Combine(_root, "profile.json")));
        var node = new NodeViewModel(Endpoint(app.Services, identity.Fingerprint()), vm.Text) { AccessToken = _key };
        vm.Nodes.Add(node);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), value => value.Command == vm.PairNodeCommand);
            Assert.Same(node, button.CommandParameter);
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)button.Command!).ExecuteAsync(button.CommandParameter);
            Assert.StartsWith(vm.Text["NodePaired"], node.Status);
            var read = Assert.Single(window.GetVisualDescendants().OfType<Button>(), value => value.Command == vm.ReadRemoteRpcCommand);
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)read.Command!).ExecuteAsync(read.CommandParameter);
            Assert.StartsWith(vm.Text["RemoteRpcStopped"], node.Status);
            node.CertificateFingerprint = new string('0', 64);
            await vm.PairNodeCommand.ExecuteAsync(node);
            Assert.StartsWith(vm.Text["ConnectionFailed"], node.Status);
            Assert.False(node.IsRemoteBusy);
        }
        finally { window.Close(); await app.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public void PlaintextOrWeakKeyCannotEnableControl()
    {
        Assert.Throws<InvalidOperationException>(() => Agent.Program.Build(["--Router:AllowRemoteControl", "true", "--Router:AccessToken", _key]));
        Assert.Throws<InvalidOperationException>(() => Agent.Program.Build(
            ["--Router:AllowRemoteControl", "true", "--Router:UseTls", "true", "--Router:AccessToken", "short"]));
        Assert.Throws<ArgumentException>(() => LocalAgentHost.ValidateSettings(new() { AllowRemoteControl = true }, _key));
    }

    [Fact]
    public async Task BundledChildAgentCanStartWithProtectedTlsIdentity()
    {
        var identity = new AgentCertificateStore(Path.Combine(_root, "child-identity.dat"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var host = new LocalAgentHost(typeof(Agent.Program).Assembly.Location, identity);
        var snapshot = await host.StartAsync(new() { Port = port, UseTls = true, AllowRemoteControl = true, LocalModelPath = "locally-selected.gguf" }, _key,
            TestContext.Current.CancellationToken);
        Assert.True(host.IsRunning);
        Assert.Equal(Environment.MachineName, snapshot.HostName);
        Assert.True(snapshot.LocalModelConfigured);
        host.Stop();
        Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", port, TestContext.Current.CancellationToken)).IsOpen);
    }

    [Fact]
    public async Task LocalModelPreparationUsesPinnedTlsAndRejectsWrongDigestBeforeWritingCache()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "node.gguf");
        File.WriteAllBytes(file, [71, 71, 85, 70, 3, 0, 0, 0, 1, 2, 3]);
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Worker);
        var store = new LlamaInstallationStore(Path.Combine(_root, "runtime"));
        store.Save(new(profile.LlamaReleaseTag, LlamaBackend.Cpu, "fixture", profile.RuntimeDirectory, "fixture build 12318"));
        var identity = new AgentCertificateStore(Path.Combine(_root, "model-identity.dat"));
        var cache = new RpcModelCache(Path.Combine(_root, "cache"), (_, _) => [new(8, 3)]);
        await using var app = Agent.Program.Build([.. Arguments(identity, remote: true), "--Router:LocalModelPath", file], services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(cache);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = new NodeAgentClient();
        var node = Endpoint(app.Services, identity.Fingerprint());
        Assert.True((await client.ProbeAsync(node, _key, TestContext.Current.CancellationToken)).LocalModelConfigured);
        var expected = await RpcModelCache.IdentifyAsync(file, TestContext.Current.CancellationToken);
        var wrong = new LocalModelIdentity { Files = [new() { Length = 11, Sha256 = new string('0', 64) }] };
        var error = await Assert.ThrowsAsync<RpcException>(() => client.PrepareLocalModelAsync(node, _key, wrong, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Contains("SHA-256", error.Status.Detail);
        Assert.False(Directory.Exists(cache.CacheRoot));
        var result = await client.PrepareLocalModelAsync(node, _key, expected, TestContext.Current.CancellationToken);
        Assert.True(result.Configured);
        RpcModelCache.RequireMatch(expected, result.Identity);
        Assert.Equal(0, result.CachedTensors);
        await Assert.ThrowsAsync<RpcException>(() => client.PrepareLocalModelAsync(node, new string('0', 64), expected, TestContext.Current.CancellationToken));
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [AvaloniaFact]
    public async Task CoordinatorDoesNotStartModelWhenWorkerCopyDiffers()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "different.gguf");
        File.WriteAllBytes(file, [71, 71, 85, 70, 3, 0, 0, 0, 9]);
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        var store = new LlamaInstallationStore(Path.Combine(_root, "runtime"));
        store.Save(new(profile.LlamaReleaseTag, LlamaBackend.Cpu, "fixture", profile.RuntimeDirectory, "fixture build 12318"));
        var identity = new AgentCertificateStore(Path.Combine(_root, "coordinator-identity.dat"));
        await using var app = Agent.Program.Build([.. Arguments(identity, remote: true), "--Router:LocalModelPath", file], services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(new RpcModelCache(Path.Combine(_root, "cache"), (_, _) => []));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var modelStarts = 0;
        var runner = EngineProcessTests.CreateRunner();
        using var engine = new EngineProcessHost(new EngineCommandRunner(plan =>
        {
            if (plan.Arguments.Contains("--model")) modelStarts++;
            return runner.StartFactory(plan);
        }));
        using var vm = new MainWindowViewModel(new ProfileStore(Path.Combine(_root, "profile.json")), engineHost: engine)
        { RuntimeDirectory = profile.RuntimeDirectory, ModelPath = profile.ModelPath, LlamaBackendIndex = 1, ApiPort = profile.ApiPort };
        vm.Nodes.Add(new(Endpoint(app.Services, identity.Fingerprint()), vm.Text) { AccessToken = _key });
        await vm.StartEngineCommand.ExecuteAsync(null);
        Assert.Equal(0, modelStarts);
        Assert.False(vm.IsEngineRunning);
        Assert.Contains("SHA-256", vm.EngineStatus);
        Assert.False(vm.IsEngineBusy);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    // Передаёт тестовую конфигурацию через аргументы только внутри изолированного процесса тестов
    private string[] Arguments(AgentCertificateStore identity, bool remote = false) => ["--Router:Port", "0", "--Router:UseTls", "true",
        "--Router:CertificatePath", identity.FilePath, "--Router:AccessToken", _key, "--Router:AllowRemoteControl", remote.ToString()];

    // Читает порт, реально выделенный Kestrel для конкретного тестового TLS-сервера
    private static NodeEndpoint Endpoint(IServiceProvider services, string pin)
    {
        var uri = new Uri(services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return new() { Name = "TLS fixture", Host = uri.Host, AgentPort = uri.Port, RpcPort = uri.Port == 50052 ? 50053 : 50052,
            UseTls = true, CertificateFingerprint = pin };
    }

    /// <summary>Удаляет только созданные этим экземпляром тестовые файлы</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
