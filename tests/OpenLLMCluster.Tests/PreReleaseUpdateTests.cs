// Проверяет сохранность данных при обновлении, повторный запуск и отмену при работающем движке
namespace OpenLLMCluster.Tests;

public sealed class PreReleaseUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "olc-prerelease-" + Guid.NewGuid().ToString("N"));

    [AvaloniaTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ReopeningAfterPreparationRespectsAllPersistenceSwitchesAndPreservesExternalData(bool remember, bool keys)
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator) with { ContextLength = 4096, Language = "en" };
        var store = new ProfileStore(Path.Combine(_directory, "profile.json"));
        var identity = new AgentCertificateStore(Path.Combine(_directory, "agent-identity.dat"));
        var fingerprint = identity.Fingerprint();
        store.Save(profile);
        var files = new[] { identity.FilePath, profile.ModelPath, Path.Combine(profile.RuntimeDirectory,
            RuntimePlanner.GetExecutableName(NodeRole.Coordinator)) };
        var original = files.ToDictionary(path => path, File.ReadAllBytes);
        var adapter = new UpdateTestAdapter();
        using (var agent = new LocalAgentHost(certificateStore: identity))
        using (var vm = new MainWindowViewModel(store, agent, updateService: new ApplicationUpdateService(() => adapter)))
        {
            vm.Initialize();
            vm.RememberSettings = remember;
            vm.RememberAgentKeys = keys;
            vm.LanguageIndex = 1;
            vm.ThemeIndex = 2;
            vm.ContextLength = 8192;
            vm.ServerApiKey = "fixture-inference-secret";
            vm.LocalAgentToken = "fixture-local-secret";
            vm.Nodes.Add(new(new() { Name = "fixture", Host = "node.example", Enabled = false }, vm.Text)
                { AccessToken = "fixture-node-secret" });
            await ReadyAsync(vm);
            vm.Updates.RequestInstallCommand.Execute(null);
            await vm.Updates.ConfirmInstallCommand.ExecuteAsync(null);
            Assert.Equal(1, adapter.Applies);
        }
        using var reopenedAgent = new LocalAgentHost(certificateStore: identity);
        using var reopened = new MainWindowViewModel(store, reopenedAgent,
            updateService: new ApplicationUpdateService(() => new UpdateTestAdapter()));
        reopened.Initialize();
        Assert.Equal(remember, reopened.RememberSettings);
        Assert.Equal(remember ? "ru" : "system", store.LoadAutomaticSettings() ? store.Load().Language : "system");
        Assert.Equal(remember ? 8192 : 4096, store.Load().ContextLength);
        Assert.Equal(remember && keys ? "fixture-inference-secret" : "", reopened.ServerApiKey);
        Assert.Equal(remember && keys ? "fixture-local-secret" : "", reopened.LocalAgentToken);
        if (remember)
        {
            Assert.Equal(2, reopened.ThemeIndex);
            Assert.Equal(profile.ModelPath, reopened.ModelPath);
            Assert.Equal(profile.RuntimeDirectory, reopened.RuntimeDirectory);
            Assert.Equal(keys ? "fixture-node-secret" : "", Assert.Single(reopened.Nodes).AccessToken);
        }
        Assert.False(reopened.IsEngineRunning);
        Assert.False(reopened.IsLocalAgentRunning);
        Assert.Equal(fingerprint, identity.Fingerprint());
        foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.DoesNotContain("fixture-inference-secret", File.ReadAllText(store.FilePath));
        Assert.DoesNotContain("fixture-node-secret", File.ReadAllText(store.FilePath));
    }

    [AvaloniaTheory]
    [InlineData(NodeRole.Coordinator)]
    [InlineData(NodeRole.Worker)]
    public async Task DownloadCancellationAndLaterKeepActualEngineAndRpcRunning(NodeRole role)
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(role);
        var store = new ProfileStore(Path.Combine(_directory, "profile.json"));
        store.Save(profile);
        var adapter = new UpdateTestAdapter { DownloadWait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var engine = new EngineProcessHost(EngineProcessTests.CreateRunner());
        using var vm = new MainWindowViewModel(store, engineHost: engine, updateService: new ApplicationUpdateService(() => adapter));
        vm.Initialize();
        await vm.StartEngineCommand.ExecuteAsync(null);
        Assert.True(engine.IsRunning, vm.EngineStatus);
        var port = role == NodeRole.Worker ? profile.RpcPort : profile.ApiPort;
        await vm.Updates.CheckCommand.ExecuteAsync(null);
        var downloading = vm.Updates.DownloadCommand.ExecuteAsync(null);
        vm.Updates.CancelCommand.Execute(null);
        await downloading;
        Assert.True(adapter.DownloadCancelled);
        Assert.True(engine.IsRunning);
        Assert.True((await new NetworkPortProbe().CheckAsync("127.0.0.1", port, TestContext.Current.CancellationToken)).IsOpen);
        adapter.DownloadWait = null;
        await vm.Updates.DownloadCommand.ExecuteAsync(null);
        vm.Updates.RequestInstallCommand.Execute(null);
        Assert.Equal(vm.Text["UpdateConfirmBusy"], vm.Updates.ConfirmationText);
        vm.Updates.DismissConfirmationCommand.Execute(null);
        Assert.False(vm.Updates.IsConfirmationVisible);
        Assert.True(engine.IsRunning);
        Assert.Equal(0, adapter.Applies);
        Assert.True((await new NetworkPortProbe().CheckAsync("127.0.0.1", port, TestContext.Current.CancellationToken)).IsOpen);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableOriginalSettingsBlockApplyWithoutOverwritingData(bool startupFile)
    {
        var store = new ProfileStore(Path.Combine(_directory, "profile.json"));
        store.Save(new());
        var path = startupFile ? store.StartupFilePath : store.FilePath;
        File.WriteAllText(path, "{fixture-broken-json");
        var original = File.ReadAllBytes(path);
        var adapter = new UpdateTestAdapter();
        using var vm = new MainWindowViewModel(store, updateService: new ApplicationUpdateService(() => adapter));
        vm.Initialize();
        await ReadyAsync(vm);
        vm.Updates.RequestInstallCommand.Execute(null);
        await vm.Updates.ConfirmInstallCommand.ExecuteAsync(null);
        Assert.Equal(0, adapter.Applies);
        Assert.False(vm.IsPreparingUpdate);
        Assert.True(vm.CanUseWorkspace);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(vm.Text["UpdateInstallError"], vm.Updates.StatusText);
    }

    // Готовит пакет теми же командами, которые доступны в интерфейсе
    private static async Task ReadyAsync(MainWindowViewModel vm)
    {
        await vm.Updates.CheckCommand.ExecuteAsync(null);
        await vm.Updates.DownloadCommand.ExecuteAsync(null);
    }

    /// <summary>Удаляет только изолированные данные этого теста</summary>
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
