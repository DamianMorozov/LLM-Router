// Проверяет защищённый ключ inference, индикаторы готовности обеих ролей и запуск через повторные кнопки обзора
namespace OpenLLMCluster.Tests;

public sealed class ReadinessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "olc-readiness-" + Guid.NewGuid().ToString("N"));
    private ProfileStore Store => new(Path.Combine(_root, "profile.json"));

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void TopologyBackgroundTracksRoleInBothThemes(int theme)
    {
        ((App)Application.Current!).ApplyTheme(theme);
        using var vm = new MainWindowViewModel(Store);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var card = window.FindControl<Border>("TopologyCard");
            Assert.NotNull(card);
            var mainBackground = card.Background!.ToString();
            vm.IsCoordinatorMode = false;
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(mainBackground, card.Background!.ToString());
            Assert.Contains("active", window.FindControl<Border>("WorkerTopologyTile")!.Classes);
            Assert.DoesNotContain("active", window.FindControl<Border>("CoordinatorTopologyTile")!.Classes);
            vm.IsCoordinatorMode = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(mainBackground, card.Background!.ToString());
        }
        finally { window.Close(); ((App)Application.Current!).ApplyTheme(0); }
    }

    [AvaloniaFact]
    public void JournalIsLastTab()
    {
        using var vm = new MainWindowViewModel(Store);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var tabs = window.FindControl<TabControl>("WorkspaceTabs")!;
            Assert.Same(window.FindControl<TabItem>("JournalTab"), tabs.Items.Last());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InferenceKeySurvivesWindowLifecycleWithoutPlainTextOrJournalLeaks()
    {
        var app = (App)Application.Current!;
        const string key = "inference-test-key-do-not-store-in-plaintext";
        var window = app.CreateMainWindow(Store);
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        vm.ServerApiKey = key;
        window.Close();
        var reopened = app.CreateMainWindow(Store);
        try
        {
            var restored = (MainWindowViewModel)reopened.DataContext!;
            Assert.Equal(key, restored.ServerApiKey);
            Assert.DoesNotContain(key, File.ReadAllText(Store.FilePath));
            Assert.DoesNotContain(key, Encoding.UTF8.GetString(File.ReadAllBytes(Store.Secrets.FilePath)));
            restored.Events.Add("request " + key);
            Assert.DoesNotContain(key, restored.GetJournalText());
        }
        finally { reopened.Close(); }
    }

    [AvaloniaFact]
    public void InferenceKeyObeysBothPersistenceSwitchesAndOlderSecretFiles()
    {
        using (var vm = new MainWindowViewModel(Store) { ServerApiKey = "keep-inference-key" }) vm.SaveOnClose();
        using (var restored = new MainWindowViewModel(Store))
        {
            restored.Initialize();
            Assert.Equal("keep-inference-key", restored.ServerApiKey);
            var previous = File.ReadAllBytes(Store.Secrets.FilePath);
            restored.RememberSettings = false;
            restored.ServerApiKey = "unsaved-key";
            restored.SaveOnClose();
            Assert.Equal(previous, File.ReadAllBytes(Store.Secrets.FilePath));
        }
        using (var ignored = new MainWindowViewModel(Store))
        {
            ignored.Initialize();
            Assert.Empty(ignored.ServerApiKey);
            ignored.RememberSettings = true;
            ignored.RememberAgentKeys = false;
            ignored.SaveOnClose();
        }
        Assert.Empty(Store.Secrets.Load());
        using (var reopened = new MainWindowViewModel(Store)) { reopened.Initialize(); Assert.Empty(reopened.ServerApiKey); }
        Store.Save(new());
        Store.Secrets.Save(new() { ["local"] = "legacy-local-key" });
        using var legacy = new MainWindowViewModel(Store);
        legacy.Initialize();
        Assert.Empty(legacy.ServerApiKey);
        Assert.Equal("legacy-local-key", legacy.LocalAgentToken);
    }

    [AvaloniaTheory]
    [InlineData(NodeRole.Coordinator, "Готовность", 1)]
    [InlineData(NodeRole.Worker, "Готовность", 1)]
    [InlineData(NodeRole.Coordinator, "Readiness", 2)]
    [InlineData(NodeRole.Worker, "Readiness", 2)]
    public void BothRolesShowReadOnlyIndicatorsAndReuseExistingCommands(NodeRole role, string title, int language)
    {
        using var vm = new MainWindowViewModel(Store) { RoleIndex = (int)role, LanguageIndex = language };
        var window = new MainWindow { DataContext = vm, Width = 980 };
        window.Show();
        try
        {
            Assert.True(window.FindControl<Border>("OverviewModelCard")!.IsEffectivelyVisible);
            Assert.Equal(title, window.FindControl<TextBlock>("ReadinessTitle")!.Text);
            var engine = window.FindControl<ToggleSwitch>("EngineReadyIndicator");
            var agent = window.FindControl<ToggleSwitch>("AgentReadyIndicator");
            Assert.NotNull(engine);
            Assert.NotNull(agent);
            Assert.False(engine.IsEnabled);
            Assert.False(agent.IsEnabled);
            Assert.False(engine.IsChecked);
            Assert.False(agent.IsChecked);
            Assert.Same(vm.StartEngineCommand, window.FindControl<Button>("OverviewStartEngineButton")!.Command);
            Assert.Same(vm.StopEngineCommand, window.FindControl<Button>("OverviewStopEngineButton")!.Command);
            Assert.Same(vm.StartLocalAgentCommand, window.FindControl<Button>("OverviewStartAgentButton")!.Command);
            Assert.Same(vm.StopLocalAgentCommand, window.FindControl<Button>("OverviewStopAgentButton")!.Command);
            vm.IsEngineRunning = true;
            vm.IsLocalAgentRunning = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(engine.IsChecked);
            Assert.True(agent.IsChecked);
            Assert.False(window.FindControl<Button>("OverviewStartEngineButton")!.IsEffectivelyEnabled);
            Assert.True(window.FindControl<Button>("OverviewStopEngineButton")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<Button>("OverviewStartAgentButton")!.IsEffectivelyEnabled);
            Assert.True(window.FindControl<Button>("OverviewStopAgentButton")!.IsEffectivelyEnabled);
            vm.IsEngineRunning = false;
            vm.IsLocalAgentRunning = false;
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(NodeRole.Coordinator)]
    [InlineData(NodeRole.Worker)]
    public async Task OverviewAgentButtonsStartAuthenticatedAgentAndStopItsPort(NodeRole role)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var host = new LocalAgentHost(typeof(Agent.Program).Assembly.Location);
        using var vm = new MainWindowViewModel(Store, agentHost: host) { RoleIndex = (int)role, LocalAgentPort = port };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var start = window.FindControl<Button>("OverviewStartAgentButton")!;
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)start.Command!).ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(host.IsRunning, vm.LocalAgentStatus);
            Assert.True(window.FindControl<ToggleSwitch>("AgentReadyIndicator")!.IsChecked);
            window.FindControl<Button>("OverviewStopAgentButton")!.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(host.IsRunning);
            Assert.False(window.FindControl<ToggleSwitch>("AgentReadyIndicator")!.IsChecked);
            Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", port, TestContext.Current.CancellationToken)).IsOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(NodeRole.Coordinator)]
    [InlineData(NodeRole.Worker)]
    public async Task OverviewEngineButtonsStartActualFixtureAndStopOwnedPort(NodeRole role)
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(role);
        Store.Save(profile);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner());
        using var vm = new MainWindowViewModel(Store, engineHost: host);
        vm.Initialize();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var button = window.FindControl<Button>("OverviewStartEngineButton");
            Assert.NotNull(button);
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)button.Command!).ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(host.IsRunning, vm.EngineStatus);
            Assert.True(window.FindControl<ToggleSwitch>("EngineReadyIndicator")!.IsChecked);
            if (role == NodeRole.Coordinator)
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new("Bearer", vm.ServerApiKey);
                using var models = JsonDocument.Parse(await client.GetStringAsync(new Uri(host.BaseUri!, "v1/models"), TestContext.Current.CancellationToken));
                Assert.Contains(models.RootElement.GetProperty("data").EnumerateArray(), model => model.GetProperty("id").GetString() == vm.ModelId);
                var copy = window.FindControl<Button>("CopyModelIdButton")!;
                await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)copy.Command!).ExecuteAsync(copy.CommandParameter);
                Assert.Equal(vm.ModelId, await window.Clipboard!.TryGetTextAsync());
            }
            window.FindControl<Button>("OverviewStopEngineButton")!.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(host.IsRunning);
            Assert.False(window.FindControl<ToggleSwitch>("EngineReadyIndicator")!.IsChecked);
            var port = role == NodeRole.Coordinator ? profile.ApiPort : profile.RpcPort;
            Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", port, TestContext.Current.CancellationToken)).IsOpen);
        }
        finally { window.Close(); }
    }

    /// <summary>Удаляет только изолированные файлы тестов</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
