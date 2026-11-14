// Проверяет кнопки поиска и журнала через XAML, выбор роли, буфер обмена и сохранение ручных значений
namespace OpenLLMCluster.Tests;

public sealed class DiscoveryDesktopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "open-llm-cluster-discovery-ui-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task SearchButtonsApplyRuntimeAndModelDirectoryWithoutChoosingModelFile()
    {
        var modelDirectory = Path.Combine(_root, "models");
        Directory.CreateDirectory(modelDirectory);
        var studio = Path.Combine(_root, "home", ".lmstudio");
        Directory.CreateDirectory(studio);
        File.WriteAllText(Path.Combine(studio, "settings.json"), JsonSerializer.Serialize(new { downloadsFolder = modelDirectory }));
        var runtime = CreateBackend(hasRpc: true);
        var vm = CreateViewModel();
        vm.ModelPath = "manual.gguf";
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("RuntimeTab");
            await ExecuteButtonAsync(window, "FindRuntimeButton");
            await ExecuteButtonAsync(window, "FindModelsButton");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(runtime, vm.RuntimeDirectory);
            Assert.Equal(modelDirectory, vm.ModelDirectory);
            Assert.Equal("manual.gguf", vm.ModelPath);
            Assert.Same(vm.SelectedRuntimeLocation, window.FindControl<ComboBox>("RuntimeLocationsSelector")!.SelectedItem);
            Assert.Equal(vm.Text["RuntimeDiscovered"], vm.RuntimeDiscoveryStatus);
            Assert.Equal(vm.Text["ModelsDiscovered"], vm.ModelDiscoveryStatus);
            vm.LanguageIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Find automatically", window.FindControl<Button>("FindRuntimeButton")!.Content);
            Assert.Equal(vm.Text["RuntimeDiscovered"], vm.RuntimeDiscoveryStatus);
            Assert.Equal(vm.Text["ModelsDiscovered"], vm.ModelDiscoveryStatus);
            SaveFrame(window, "discovery-en.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task StudioServerAloneDoesNotBecomeWorkerRpcAndMissingModelsKeepManualPath()
    {
        CreateBackend(hasRpc: false);
        var vm = CreateViewModel();
        vm.RoleIndex = (int)NodeRole.Worker;
        vm.RuntimeDirectory = "manual runtime";
        vm.ModelDirectory = "manual models";
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("RuntimeTab");
            await ExecuteButtonAsync(window, "FindRuntimeButton");
            Assert.False(window.FindControl<Button>("FindModelsButton")!.IsEffectivelyEnabled);
            Assert.False(vm.FindModelsCommand.CanExecute(null));
            Assert.Null(vm.SelectedRuntimeLocation);
            Assert.Equal("manual runtime", vm.RuntimeDirectory);
            Assert.Equal("manual models", vm.ModelDirectory);
            Assert.Equal(vm.Text["RuntimeRoleMissing"], vm.RuntimeDiscoveryStatus);
            Assert.Empty(vm.ModelDiscoveryStatus);
            Assert.False(Assert.Single(vm.RuntimeLocations).HasRpc);
            SaveFrame(window, "discovery-worker-ru.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task JournalButtonsCopySanitizedSnapshotAndClearOnlyEvents()
    {
        var vm = CreateViewModel();
        vm.LocalAgentToken = "local-secret";
        vm.AddNodeCommand.Execute(null);
        vm.Nodes[0].AccessToken = "node-secret";
        vm.Events.Add("first: local-secret");
        vm.Events.Add("second: node-secret");
        vm.ServerApiKey = "inference-secret";
        vm.EngineEvents.Add("engine: inference-secret");
        vm.Checks.Add("keep diagnostic");
        vm.LocalAgentStatus = "keep agent status";
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("JournalTab");
            Dispatcher.UIThread.RunJobs();
            var clipboard = window.Clipboard;
            Assert.NotNull(clipboard);
            await ExecuteButtonAsync(window, "CopyJournalButton");
            var text = await clipboard.TryGetTextAsync();
            Assert.Equal("first: [token]" + Environment.NewLine + "second: [token]" + Environment.NewLine + "engine: [token]", text);
            Assert.Equal(2, vm.Events.Count);
            Assert.Equal(vm.Text["JournalCopied"], vm.Status);
            SaveFrame(window, "journal-ru.png");
            var clear = window.FindControl<Button>("ClearJournalButton")!;
            Assert.NotNull(clear.Command);
            clear.Command.Execute(clear.CommandParameter);
            Assert.Empty(vm.Events);
            Assert.Empty(vm.EngineEvents);
            Assert.Equal("keep diagnostic", Assert.Single(vm.Checks));
            Assert.Equal("keep agent status", vm.LocalAgentStatus);
            Assert.Equal("local-secret", vm.LocalAgentToken);
            await ExecuteButtonAsync(window, "CopyJournalButton");
            Assert.Equal("", await clipboard.TryGetTextAsync());
        }
        finally { window.Close(); }
    }

    // Подставляет изолированные домашний каталог, каталог приложения и PATH
    private MainWindowViewModel CreateViewModel() => new(new ProfileStore(Path.Combine(_root, "profile.json")),
        installationDiscovery: new LocalInstallationDiscovery(Path.Combine(_root, "home"), Path.Combine(_root, "app"), "", Path.Combine(_root, "appdata")));

    // Создаёт только файлы-маркеры для поиска, не запускаемые исполняемые файлы
    private string CreateBackend(bool hasRpc)
    {
        var directory = Path.Combine(_root, "home", ".lmstudio", "extensions", "backends", "llama.cpp-test");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, RuntimePlanner.GetExecutableName(NodeRole.Coordinator)), "test fixture");
        if (hasRpc) File.WriteAllText(Path.Combine(directory, RuntimePlanner.GetExecutableName(NodeRole.Worker)), "test fixture");
        return directory;
    }

    // Исполняет именно команду кнопки с параметром, заданным в XAML
    private static async Task ExecuteButtonAsync(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var button = window.FindControl<Button>(name)!;
        var command = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(button.Command);
        Assert.True(command.CanExecute(button.CommandParameter));
        await command.ExecuteAsync(button.CommandParameter);
    }

    // Сохраняет рендер для проверки расположения новых элементов
    private static void SaveFrame(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var directory = Path.Combine(Path.GetTempPath(), "OpenLLMCluster.Tests", "screenshots");
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame()!;
        frame.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    /// <summary>Удаляет временное окружение теста</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
