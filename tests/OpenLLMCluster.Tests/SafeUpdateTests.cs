// Проверяет подключение подтверждения к безопасной подготовке и отсутствие установки до явного согласия
namespace OpenLLMCluster.Tests;

public sealed class SafeUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "olc-safe-update-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task MainWindowConnectsInstallerOnlyAfterSeparateConfirmation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "olc-apply-" + Guid.NewGuid().ToString("N"));
        var store = new ProfileStore(Path.Combine(directory, "profile.json"));
        using var vm = new MainWindowViewModel(store, updateService: new ApplicationUpdateService(() => new UpdateTestAdapter()));
        Assert.True(vm.Updates.IsInstallationSupported);
        await vm.Updates.CheckCommand.ExecuteAsync(null);
        await vm.Updates.DownloadCommand.ExecuteAsync(null);
        Assert.False(File.Exists(store.FilePath));
        vm.Updates.RequestInstallCommand.Execute(null);
        Assert.True(vm.Updates.CanConfirmInstall);
        Assert.False(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedValidationCannotPrepareOrApplyAndRetryWorks(bool validationFails)
    {
        var adapter = new UpdateTestAdapter();
        var service = new ApplicationUpdateService(() => adapter);
        await ReadyAsync(service);
        var preparations = 0;
        adapter.ValidationFailure = validationFails ? new InvalidDataException("fixture corrupted package") : null;
        await Assert.ThrowsAnyAsync<Exception>(() => service.ApplyAsync(_ =>
        {
            preparations++;
            return Task.FromException(new IOException("fixture preparation failed"));
        }, TestContext.Current.CancellationToken));
        Assert.Equal(validationFails ? 0 : 1, preparations);
        Assert.Equal(0, adapter.Applies);
        Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
        Assert.Equal("UpdateInstallError", service.Status.MessageKey);
        adapter.ValidationFailure = null;
        await service.ApplyAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Equal(1, adapter.Applies);
    }

    [Fact]
    public async Task ApplyRequiresReadyAndRejectsConcurrentOperations()
    {
        var adapter = new UpdateTestAdapter();
        var service = new ApplicationUpdateService(() => adapter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken));
        await ReadyAsync(service);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var apply = service.ApplyAsync(ct => pending.Task.WaitAsync(ct), cancellation.Token);
        Assert.False(await service.CheckAsync(TestContext.Current.CancellationToken));
        Assert.False(await service.DownloadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        Assert.Equal(0, adapter.Applies);
        Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
        Assert.False(service.IsBusy);
    }

    [AvaloniaTheory]
    [InlineData(NodeRole.Coordinator)]
    [InlineData(NodeRole.Worker)]
    public async Task ConfirmStopsOwnedProcessesAndSavesBeforeUpdaterButLeavesOtherListeners(NodeRole role)
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(role);
        var store = CreateStore();
        store.Save(profile);
        var adapter = new UpdateTestAdapter();
        using var engine = new EngineProcessHost(EngineProcessTests.CreateRunner());
        using var agent = new LocalAgentHost(typeof(Agent.Program).Assembly.Location);
        using var vm = new MainWindowViewModel(store, agent, engineHost: engine, updateService: new ApplicationUpdateService(() => adapter));
        vm.Initialize();
        vm.LocalAgentPort = FreePort();
        vm.LocalAgentToken = LocalAgentHost.GenerateToken();
        vm.ServerApiKey = LocalAgentHost.GenerateToken();
        var modelBytes = File.ReadAllBytes(profile.ModelPath);
        var runtimeBytes = File.ReadAllBytes(Path.Combine(profile.RuntimeDirectory, RuntimePlanner.GetExecutableName(role)));
        using var unrelated = new TcpListener(IPAddress.Loopback, 0);
        unrelated.Start();
        var otherPort = ((IPEndPoint)unrelated.LocalEndpoint).Port;
        var ownedPort = role == NodeRole.Worker ? profile.RpcPort : profile.ApiPort;
        var captured = false;
        vm.CaptureWindowSettings = () => { captured = true; vm.WindowGeometry = new() { Width = 1150, Height = 750, X = 30, Y = 40 }; };
        await vm.StartEngineCommand.ExecuteAsync(null);
        await vm.StartLocalAgentCommand.ExecuteAsync(null);
        Assert.True(engine.IsRunning, vm.EngineStatus);
        Assert.True(agent.IsRunning, vm.LocalAgentStatus);
        await vm.Updates.CheckCommand.ExecuteAsync(null);
        await vm.Updates.DownloadCommand.ExecuteAsync(null);
        Assert.True(engine.IsRunning);
        Assert.True(agent.IsRunning);
        vm.Updates.RequestInstallCommand.Execute(null);
        Assert.Equal(vm.Text["UpdateConfirmBusy"], vm.Updates.ConfirmationText);
        adapter.OnApply = () =>
        {
            Assert.True(captured);
            Assert.False(engine.IsRunning);
            Assert.False(agent.IsRunning);
            Assert.True(vm.IsPreparingUpdate);
            Assert.False(vm.StartEngineCommand.CanExecute(null));
            Assert.False(vm.StartLocalAgentCommand.CanExecute(null));
            Assert.Equal(1150, store.Load().Window.Width);
            Assert.Equal(vm.ServerApiKey, store.Secrets.Load()["inference"]);
            Assert.Equal(vm.LocalAgentToken, store.Secrets.Load()["local"]);
            Assert.DoesNotContain(vm.ServerApiKey, File.ReadAllText(store.FilePath));
            Assert.Equal(modelBytes, File.ReadAllBytes(profile.ModelPath));
            Assert.Equal(runtimeBytes, File.ReadAllBytes(Path.Combine(profile.RuntimeDirectory, RuntimePlanner.GetExecutableName(role))));
        };
        // Ошибка запуска обновлятора позволяет проверить восстановление интерфейса без завершения тестового процесса
        adapter.ApplyFailure = new IOException("fixture updater start failed");
        await vm.Updates.ConfirmInstallCommand.ExecuteAsync(null);
        Assert.False(vm.IsPreparingUpdate);
        Assert.True(vm.CanUseWorkspace);
        Assert.Equal(0, adapter.Applies);
        Assert.Contains(vm.Events, message => message.Contains("fixture updater start failed", StringComparison.Ordinal));
        Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", ownedPort, TestContext.Current.CancellationToken)).IsOpen);
        Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", vm.LocalAgentPort, TestContext.Current.CancellationToken)).IsOpen);
        Assert.True((await new NetworkPortProbe().CheckAsync("127.0.0.1", otherPort, TestContext.Current.CancellationToken)).IsOpen);
        adapter.ApplyFailure = null;
        await vm.Updates.ConfirmInstallCommand.ExecuteAsync(null);
        Assert.Equal(1, adapter.Applies);
    }

    [AvaloniaFact]
    public async Task SaveFailureBlocksInstallerAndRestoresControls()
    {
        var store = CreateStore();
        var adapter = new UpdateTestAdapter();
        using var vm = new MainWindowViewModel(store, updateService: new ApplicationUpdateService(() => adapter));
        Directory.CreateDirectory(store.FilePath);
        await ConfirmAsync(vm);
        Assert.Equal(0, adapter.Applies);
        Assert.False(vm.IsPreparingUpdate);
        Assert.True(vm.CanUseWorkspace);
        Assert.Equal(vm.Text["UpdateInstallError"], vm.Updates.StatusText);
        Directory.Delete(store.FilePath);
        await vm.Updates.ConfirmInstallCommand.ExecuteAsync(null);
        Assert.Equal(1, adapter.Applies);
        Assert.True(File.Exists(store.FilePath));
    }

    [AvaloniaFact]
    public async Task PreparationBlocksWorkspaceAndWindowCloseUntilFailureIsReported()
    {
        var adapter = new UpdateTestAdapter { ValidationWait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var vm = new MainWindowViewModel(CreateStore(), updateService: new ApplicationUpdateService(() => adapter));
        var window = new MainWindow { DataContext = vm };
        window.ConfigureSettings(vm);
        window.Show();
        await vm.Updates.CheckCommand.ExecuteAsync(null);
        await vm.Updates.DownloadCommand.ExecuteAsync(null);
        vm.Updates.RequestInstallCommand.Execute(null);
        var pending = vm.Updates.ConfirmInstallCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.True(vm.IsPreparingUpdate);
            Assert.False(window.FindControl<TabItem>("OverviewTab")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<TabItem>("RuntimeTab")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<TabItem>("LocalAgentTab")!.IsEffectivelyEnabled);
            Assert.False(vm.Updates.CanCancel);
            window.Close();
            Assert.True(window.IsVisible);
            adapter.ValidationWait.SetException(new InvalidDataException("fixture checksum failed"));
            await pending;
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsPreparingUpdate);
            Assert.True(window.FindControl<TabItem>("OverviewTab")!.IsEffectivelyEnabled);
            Assert.Equal(0, adapter.Applies);
        }
        finally { adapter.ValidationWait.TrySetException(new IOException("fixture cleanup")); await pending; window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UpdateRespectsRememberSwitches(bool rememberSettings, bool rememberKeys)
    {
        var store = CreateStore();
        store.Save(new() { ContextLength = 4096 });
        store.Secrets.Save(new Dictionary<string, string> { ["inference"] = "old-key" });
        var original = File.ReadAllBytes(store.FilePath);
        var secrets = File.ReadAllBytes(store.FilePath + ".secrets");
        var adapter = new UpdateTestAdapter();
        using var vm = new MainWindowViewModel(store, updateService: new ApplicationUpdateService(() => adapter))
        { RememberSettings = rememberSettings, RememberAgentKeys = rememberKeys, ContextLength = 8192, ServerApiKey = "new-secret" };
        await ConfirmAsync(vm);
        Assert.Equal(1, adapter.Applies);
        Assert.Equal(rememberSettings, store.LoadAutomaticSettings());
        if (!rememberSettings)
        {
            Assert.Equal(original, File.ReadAllBytes(store.FilePath));
            Assert.Equal(secrets, File.ReadAllBytes(store.FilePath + ".secrets"));
        }
        else
        {
            Assert.Equal(8192, store.Load().ContextLength);
            Assert.Empty(store.Secrets.Load());
        }
    }

    // Выбирает обновление и готовит пакет без применения
    private static async Task ReadyAsync(ApplicationUpdateService service)
    {
        await service.CheckAsync(TestContext.Current.CancellationToken);
        await service.DownloadAsync(TestContext.Current.CancellationToken);
    }

    // Вызывает ту же последовательность команд, что и отдельные кнопки интерфейса
    private static async Task ConfirmAsync(MainWindowViewModel vm)
    {
        await vm.Updates.CheckCommand.ExecuteAsync(null);
        await vm.Updates.DownloadCommand.ExecuteAsync(null);
        vm.Updates.RequestInstallCommand.Execute(null);
        await vm.Updates.ConfirmInstallCommand.ExecuteAsync(null);
    }

    // Создаёт изолированное хранилище без обращения к профилю пользователя
    private ProfileStore CreateStore() => new(Path.Combine(_directory, Guid.NewGuid().ToString("N"), "profile.json"));

    // Выделяет временный локальный порт
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Удаляет только собственные временные настройки</summary>
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
