// Проверяет настоящие дочерние процессы при загрузке и закрытии окна, готовность модели и сохраняемый интервал мониторинга
namespace OpenLLMCluster.Tests;

public sealed class ModelLifecycleDesktopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "open-llm-cluster-model-lifecycle-" + Guid.NewGuid().ToString("N"));
    private ProfileStore Store => new(Path.Combine(_root, "profile.json"));

    [AvaloniaTheory]
    [InlineData("exit", "ModelLoadFailed")]
    [InlineData("loading", "ModelLoadCancelled")]
    public async Task FailedAndCancelledLoadingCannotLeaveReadyProgress(string mode, string expectedStage)
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner(mode));
        using var vm = CreateViewModel(profile, host);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var start = vm.StartEngineCommand.ExecuteAsync(null);
        try
        {
            if (mode == "loading")
            {
                await WaitUntilAsync(() => host.IsRunning);
                vm.CancelEngineOperationCommand.Execute(null);
            }
            await start;
            Dispatcher.UIThread.RunJobs();
            Assert.False(host.IsRunning);
            var progress = window.FindControl<ProgressBar>("ModelLoadProgressBar")!;
            Assert.False(progress.IsIndeterminate);
            Assert.Equal(0, progress.Value);
            Assert.Equal(vm.Text[expectedStage], window.FindControl<TextBlock>("ModelLoadStageText")!.Text);
        }
        finally { window.Close(); await start; }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    public void UnsupportedSavedMonitorIntervalFallsBackToThreeSeconds(int seconds)
    {
        Store.Save(new() { MonitorIntervalSeconds = seconds });
        using var vm = new MainWindowViewModel(Store);
        vm.Initialize();
        Assert.Equal(3, vm.MonitorIntervalSeconds);
    }

    [AvaloniaFact]
    public async Task OverviewShowsActualLoadStagesAndBecomesReadyOnlyAfterApiReadiness()
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner("progress"));
        using var vm = CreateViewModel(profile, host);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var start = vm.StartEngineCommand.ExecuteAsync(null);
        try
        {
            var progress = window.FindControl<ProgressBar>("ModelLoadProgressBar");
            Assert.NotNull(progress);
            await WaitUntilAsync(() => vm.EngineEvents.Any(line => line.Contains("loading model 'fixture.gguf'")));
            Assert.True(progress.IsIndeterminate);
            Assert.False(vm.IsEngineRunning);
            Assert.Equal(vm.Text["ModelLoadingWeights"], window.FindControl<TextBlock>("ModelLoadStageText")!.Text);
            Assert.Contains(Path.GetFileName(profile.ModelPath), window.FindControl<TextBlock>("OverviewModelName")!.Text);
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", vm.ServerApiKey);
            await WaitUntilAsync(() => host.BaseUri is not null);
            await client.PostAsync(new Uri(host.BaseUri!, "fixture/load-stage?stage=context"), null);
            await WaitUntilAsync(() => window.FindControl<TextBlock>("ModelLoadStageText")!.Text == vm.Text["ModelCreatingContext"]);
            await client.PostAsync(new Uri(host.BaseUri!, "fixture/load-stage?stage=warmup"), null);
            await WaitUntilAsync(() => window.FindControl<TextBlock>("ModelLoadStageText")!.Text == vm.Text["ModelWarmingUp"]);
            Assert.True(progress.IsIndeterminate);
            await client.PostAsync(new Uri(host.BaseUri!, "fixture/load-stage?stage=ready"), null);
            await start;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsEngineRunning, vm.EngineStatus);
            Assert.False(progress.IsIndeterminate);
            Assert.Equal(100, progress.Value);
            Assert.Equal(vm.Text["ModelReady"], window.FindControl<TextBlock>("ModelLoadStageText")!.Text);
            vm.StopEngineCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, progress.Value);
            Assert.Equal(vm.Text["ModelNotLoaded"], window.FindControl<TextBlock>("ModelLoadStageText")!.Text);
        }
        finally { window.Close(); await start; }
    }

    [AvaloniaFact]
    public async Task ConfirmedCloseUnloadsModelButCancelledCloseAndForeignProcessRemainUntouched()
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        var foreignProfile = fixture.CreateProfile(NodeRole.Worker);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner());
        using var foreign = new EngineProcessHost(EngineProcessTests.CreateRunner());
        await foreign.StartAsync(foreignProfile, "", TestContext.Current.CancellationToken);
        using var vm = CreateViewModel(profile, host);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            await vm.StartEngineCommand.ExecuteAsync(null);
            Assert.True(host.IsRunning, vm.EngineStatus);
            EventHandler<WindowClosingEventArgs> cancel = (_, args) => args.Cancel = true;
            window.Closing += cancel;
            window.Close();
            Assert.True(window.IsVisible);
            Assert.True(host.IsRunning);
            window.Closing -= cancel;
            window.Close();
            Assert.False(host.IsRunning);
            Assert.False(vm.IsEngineRunning);
            Assert.False(vm.IsEngineStarting);
            Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.ApiPort)).IsOpen);
            Assert.True(foreign.IsRunning);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingDuringLoadCancelsStartAndFreesServerPort()
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner("loading"));
        using var vm = CreateViewModel(profile, host);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var start = vm.StartEngineCommand.ExecuteAsync(null);
        try
        {
            await WaitUntilAsync(() => host.IsRunning && host.BaseUri is not null);
            Assert.False(vm.IsEngineRunning);
            window.Close();
            await start;
            Assert.False(host.IsRunning);
            Assert.False(vm.IsEngineStarting);
            Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.ApiPort)).IsOpen);
        }
        finally { window.Close(); await start; }
    }

    [AvaloniaFact]
    public async Task MonitorIntervalSelectorChangesTimerAndRestoresSavedSeconds()
    {
        using var vm = new MainWindowViewModel(Store);
        var calls = 0;
        vm.LocalLoadReader = _ => { calls++; return Task.FromResult(new NodeSnapshot { HostName = "fixture", TelemetryAvailable = true }); };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var selector = window.FindControl<ComboBox>("MonitorIntervalSelector");
            Assert.NotNull(selector);
            Assert.Equal(3, selector.SelectedItem);
            selector.SelectedItem = 1;
            Dispatcher.UIThread.RunJobs();
            vm.StartMonitoring();
            await WaitUntilAsync(() => calls >= 2, seconds: 2.8);
            selector.SelectedItem = 10;
            Dispatcher.UIThread.RunJobs();
            vm.SaveOnClose();
            using var saved = JsonDocument.Parse(File.ReadAllText(Store.FilePath));
            Assert.Equal(10, saved.RootElement.GetProperty("MonitorIntervalSeconds").GetInt32());
            using var reopened = new MainWindowViewModel(Store);
            reopened.Initialize();
            var secondWindow = new MainWindow { DataContext = reopened };
            secondWindow.Show();
            try { Assert.Equal(10, secondWindow.FindControl<ComboBox>("MonitorIntervalSelector")!.SelectedItem); }
            finally { secondWindow.Close(); }
            vm.AutoMonitor = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(selector.IsEffectivelyEnabled);
            vm.AutoMonitor = true;
            Assert.Equal(10, selector.SelectedItem);
        }
        finally { window.Close(); }
    }

    // Создаёт представление с изолированным процессом вместо пользовательской модели
    private MainWindowViewModel CreateViewModel(RouterProfile profile, EngineProcessHost host) => new(Store, engineHost: host)
    {
        RoleIndex = (int)profile.Role, RuntimeDirectory = profile.RuntimeDirectory, ModelPath = profile.ModelPath,
        RpcPort = profile.RpcPort, ApiPort = profile.ApiPort, EngineStartupTimeoutSeconds = 20, AutoMonitor = false
    };

    // Ожидает наблюдаемый результат, продолжая обработку привязок интерфейса
    private static async Task WaitUntilAsync(Func<bool> condition, double seconds = 15)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(30, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "Не дождались проверяемого состояния");
    }

    /// <summary>Удаляет только временные настройки проверки</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
