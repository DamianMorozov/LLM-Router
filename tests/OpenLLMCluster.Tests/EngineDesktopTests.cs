// Проверяет установку и вычислительные процессы через команды XAML, журнал, готовность, локализацию и закрытие окна
namespace OpenLLMCluster.Tests;

public sealed class EngineDesktopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "open-llm-cluster-engine-ui-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task InstallationButtonSelectsBuildAndRecoversAfterCancellationOrFailure()
    {
        var installer = new FixtureInstaller(_root);
        using var vm = new MainWindowViewModel(Store, llamaInstaller: installer);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            SelectRuntime(window);
            installer.Failure = new OperationCanceledException();
            await ExecuteButtonAsync(window, "InstallLlamaButton");
            Assert.True(vm.CanEditEngine);
            Assert.Equal(vm.Text["AgentActionCancelled"], vm.EngineStatus);
            installer.Failure = new IOException("fixture download failed");
            await ExecuteButtonAsync(window, "InstallLlamaButton");
            Assert.Contains("fixture download failed", vm.EngineStatus);
            Assert.True(vm.InstallLlamaCommand.CanExecute(null));
            installer.Failure = null;
            vm.LlamaBackendIndex = (int)LlamaBackend.Cpu;
            await ExecuteButtonAsync(window, "InstallLlamaButton");
            Assert.Equal(3, installer.Calls);
            Assert.Equal(_root, vm.RuntimeDirectory);
            Assert.Equal(0, vm.GpuLayers);
            Assert.Contains(vm.Text["LlamaInstalled"], vm.EngineStatus);
            vm.LanguageIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(vm.Text["InstallLlama"], window.FindControl<Button>("InstallLlamaButton")!.Content);
            Assert.Contains(vm.Text["LlamaInstalled"], window.FindControl<TextBlock>("EngineStatusText")!.Text);
            SaveFrame(window, "engine-install-en.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WorkerButtonsStartStopAndClosingWindowFreesRpcPort()
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Worker);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner());
        var vm = CreateViewModel(profile, host);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            SelectRuntime(window);
            Assert.True(window.FindControl<Button>("RpcFirewallButton")!.IsVisible);
            await ExecuteButtonAsync(window, "StartEngineButton");
            Assert.True(host.IsRunning, vm.EngineStatus);
            Assert.False(vm.CanEditEngine);
            Assert.False(vm.StartEngineCommand.CanExecute(null));
            Assert.False(vm.CanTestEngine);
            Assert.Contains(vm.Text["RpcReady"], vm.EngineStatus);
            Assert.NotEmpty(vm.EngineEvents);
            var stop = window.FindControl<Button>("StopEngineButton")!;
            Assert.True(stop.Command!.CanExecute(null));
            stop.Command.Execute(null);
            Assert.False(host.IsRunning);
            Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.RpcPort)).IsOpen);
            await ExecuteButtonAsync(window, "StartEngineButton");
            Assert.True(host.IsRunning);
        }
        finally { window.Close(); }
        Assert.False(host.IsRunning);
        Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.RpcPort)).IsOpen);
    }

    [AvaloniaFact]
    public async Task MainButtonsRequireReadinessThenGenerateAndKeepApiKeyOutOfProfileAndJournal()
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner());
        var vm = CreateViewModel(profile, host);
        vm.LlamaBackendIndex = (int)LlamaBackend.Cpu;
        vm.GpuLayers = 0;
        vm.EngineStartupTimeoutSeconds = 35;
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            SelectRuntime(window);
            Assert.False(vm.TestEngineCommand.CanExecute(null));
            Assert.False(window.FindControl<Button>("RpcFirewallButton")!.IsVisible);
            await ExecuteButtonAsync(window, "StartEngineButton");
            Assert.True(vm.IsEngineRunning, vm.EngineStatus);
            Assert.Equal(64, vm.ServerApiKey.Length);
            Assert.Contains(vm.Text["InferenceReady"], vm.EngineStatus);
            Assert.Equal(vm.Text["EffortUnsupported"], vm.EffortSupport);
            await ExecuteButtonAsync(window, "TestEngineButton");
            Assert.StartsWith("READY", vm.EngineTestOutput);
            Assert.Equal(vm.Text["InferenceTestPassed"], vm.EngineStatus);
            Assert.DoesNotContain(vm.ServerApiKey, string.Join('\n', vm.Events.Concat(vm.EngineEvents)));
            vm.SaveOnClose();
            var json = File.ReadAllText(Store.FilePath);
            Assert.DoesNotContain(vm.ServerApiKey, json);
            using var restored = new MainWindowViewModel(Store);
            restored.Initialize();
            Assert.Equal(vm.LlamaReleaseTag, restored.LlamaReleaseTag);
            Assert.Equal(vm.LlamaBackendIndex, restored.LlamaBackendIndex);
            Assert.Equal(0, restored.GpuLayers);
            Assert.Equal(35, restored.EngineStartupTimeoutSeconds);
            Assert.Equal(vm.ServerApiKey, restored.ServerApiKey);
            Assert.DoesNotContain(vm.ServerApiKey, Encoding.UTF8.GetString(File.ReadAllBytes(Store.Secrets.FilePath)));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.FindControl<ScrollViewer>("RuntimeScroller")!.Offset = new Vector(0, window.FindControl<Border>("EngineControlCard")!.Bounds.Top);
            SaveFrame(window, "engine-main-ru.png");
            vm.LanguageIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(vm.Text["InferenceTestPassed"], window.FindControl<TextBlock>("EngineStatusText")!.Text);
            SaveFrame(window, "engine-main-en.png");
            vm.StopEngineCommand.Execute(null);
            Assert.True(vm.CanEditEngine);
            Assert.False(vm.CanSelectEffort);
            vm.ModelPath += ".different.gguf";
            Assert.False(vm.CanSelectEffort);
            Assert.Equal(vm.Text["EffortUnknown"], vm.EffortSupport);
        }
        finally { window.Close(); }
        Assert.False(host.IsRunning);
    }

    private ProfileStore Store => new(Path.Combine(_root, "profile.json"));

    // Подставляет изолированный процесс и реальные файлы-маркеры вместо пользовательской модели
    private MainWindowViewModel CreateViewModel(RouterProfile profile, EngineProcessHost host) => new(Store, engineHost: host)
    {
        RoleIndex = (int)profile.Role, RuntimeDirectory = profile.RuntimeDirectory, ModelPath = profile.ModelPath,
        RpcPort = profile.RpcPort, ApiPort = profile.ApiPort, EngineStartupTimeoutSeconds = 15
    };

    // Показывает вкладку, чтобы проверять фактические команды и локализованные элементы
    private static void SelectRuntime(MainWindow window)
    {
        window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("RuntimeTab");
        Dispatcher.UIThread.RunJobs();
    }

    // Выполняет привязанную к XAML команду с её настоящим параметром
    private static async Task ExecuteButtonAsync(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var button = window.FindControl<Button>(name)!;
        var command = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(button.Command);
        Assert.True(command.CanExecute(button.CommandParameter));
        await command.ExecuteAsync(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    // Сохраняет фактический headless-рендер для проверки размещения элементов
    private static void SaveFrame(MainWindow window, string name)
    {
        window.UpdateLayout();
        var directory = Path.Combine(Path.GetTempPath(), "OpenLLMCluster.Tests", "screenshots");
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame()!;
        frame.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    /// <summary>Удаляет временный профиль, не затрагивая пользовательские установки</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private sealed class FixtureInstaller(string directory) : ILlamaPackageInstaller
    {
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }

        /// <summary>Возвращает проверяемый результат установки или заданный сбой без сети</summary>
        public Task<LlamaInstallation> InstallAsync(string tag, LlamaBackend backend, IProgress<LlamaInstallProgress>? progress = null,
            CancellationToken ct = default)
        {
            Calls++;
            if (Failure is not null) return Task.FromException<LlamaInstallation>(Failure);
            return Task.FromResult(new LlamaInstallation(tag, backend, "fixture", directory, "build 12318"));
        }
    }
}
