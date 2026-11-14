// Проверяет сохранение настроек через настоящий жизненный цикл окна и переключатель в XAML
namespace OpenLLMCluster.Tests;

public sealed class WindowSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "open-llm-cluster-window-" + Guid.NewGuid().ToString("N"));
    private ProfileStore Store => new(Path.Combine(_directory, "profile.json"));

    [AvaloniaFact]
    public async Task ClosingWindowSavesEditedProfileAndReopeningRestoresIt()
    {
        var app = (App)Application.Current!;
        var window = app.CreateMainWindow(Store);
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        vm.LanguageIndex = 2;
        vm.ThemeIndex = 2;
        vm.RoleIndex = 1;
        vm.ContextLength = 32768;
        vm.ApiPort = 8099;
        vm.ModelPath = "models/example.gguf";
        vm.ModelDirectory = "models";
        vm.RuntimeDirectory = "runtime/example";
        vm.BindAddress = "0.0.0.0";
        vm.RpcPort = 50054;
        vm.CpuThreads = 6;
        vm.Nodes.Add(new(new() { Name = "worker", Host = "worker.example", Enabled = false }, vm.Text) { AccessToken = "session-secret" });
        window.Width = 1100;
        window.Height = 740;
        window.Position = new PixelPoint(30, 40);
        window.Close();
        Assert.True(File.Exists(Store.FilePath), "Закрытие окна должно сохранять профиль без ручного нажатия кнопки");
        var profile = await Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("en", profile.Language);
        Assert.Equal(AppTheme.Dark, profile.Theme);
        Assert.Equal(32768, profile.ContextLength);
        var reopened = app.CreateMainWindow(Store);
        reopened.Show();
        var restored = (MainWindowViewModel)reopened.DataContext!;
        Assert.Equal(2, restored.LanguageIndex);
        Assert.Equal(2, restored.ThemeIndex);
        Assert.Equal(1, restored.RoleIndex);
        Assert.Equal(8099, restored.ApiPort);
        Assert.Equal(vm.ModelPath, restored.ModelPath);
        Assert.Equal(vm.ModelDirectory, restored.ModelDirectory);
        Assert.Equal(vm.RuntimeDirectory, restored.RuntimeDirectory);
        Assert.Equal(vm.BindAddress, restored.BindAddress);
        Assert.Equal(vm.RpcPort, restored.RpcPort);
        Assert.Equal(vm.CpuThreads, restored.CpuThreads);
        Assert.Equal("worker.example", Assert.Single(restored.Nodes).Host);
        Assert.False(restored.Nodes[0].Enabled);
            Assert.Equal("session-secret", restored.Nodes[0].AccessToken);
        Assert.Equal(1100, reopened.Width);
        Assert.Equal(740, reopened.Height);
        Assert.Equal(new PixelPoint(30, 40), reopened.Position);
        reopened.Close();
    }

    [AvaloniaFact]
    public async Task DisabledToggleSurvivesRestartAndLeavesExistingProfileUntouched()
    {
        var app = (App)Application.Current!;
        await Store.SaveAsync(new() { Language = "en", Theme = AppTheme.Dark, ContextLength = 65536,
            Window = new() { Width = 1120, Height = 720, X = 50, Y = 60 } }, TestContext.Current.CancellationToken);
        var original = await File.ReadAllTextAsync(Store.FilePath, TestContext.Current.CancellationToken);
        var window = app.CreateMainWindow(Store);
        window.Show();
        var toggle = window.FindControl<ToggleSwitch>("RememberSettingsToggle")!;
        Assert.True(toggle.IsChecked);
        toggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(((MainWindowViewModel)window.DataContext!).RememberSettings);
        ((MainWindowViewModel)window.DataContext!).ContextLength = 16384;
        window.Close();
        Assert.Equal(original, await File.ReadAllTextAsync(Store.FilePath, TestContext.Current.CancellationToken));
        Assert.False(Store.LoadAutomaticSettings());
        var reopened = app.CreateMainWindow(Store);
        reopened.Show();
        var vm = (MainWindowViewModel)reopened.DataContext!;
        Assert.False(vm.RememberSettings);
        Assert.Equal(0, vm.LanguageIndex);
        Assert.Equal(0, vm.ThemeIndex);
        Assert.Equal(8192, vm.ContextLength);
        Assert.Equal(1240, reopened.Width);
        Assert.Equal(880, reopened.Height);
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        vm.LanguageIndex = 2;
        reopened.Close();
        Assert.Equal(original, await File.ReadAllTextAsync(Store.FilePath, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public void DisabledToggleDoesNotCreateProfileAndCanBeReenabled()
    {
        var app = (App)Application.Current!;
        var window = app.CreateMainWindow(Store);
        window.Show();
        window.FindControl<ToggleSwitch>("RememberSettingsToggle")!.IsChecked = false;
        window.Close();
        Assert.False(File.Exists(Store.FilePath));
        var reopened = app.CreateMainWindow(Store);
        reopened.Show();
        Assert.False(((MainWindowViewModel)reopened.DataContext!).RememberSettings);
        reopened.FindControl<ToggleSwitch>("RememberSettingsToggle")!.IsChecked = true;
        ((MainWindowViewModel)reopened.DataContext!).LanguageIndex = 2;
        reopened.Close();
        Assert.True(File.Exists(Store.FilePath));
        var third = app.CreateMainWindow(Store);
        third.Show();
        Assert.True(((MainWindowViewModel)third.DataContext!).RememberSettings);
        Assert.Equal(2, ((MainWindowViewModel)third.DataContext!).LanguageIndex);
        third.Close();
    }

    [AvaloniaFact]
    public async Task DisabledStartupDoesNotReadEvenCorruptProfile()
    {
        Store.SaveAutomaticSettings(enabled: false);
        await File.WriteAllTextAsync(Store.FilePath, "broken profile", TestContext.Current.CancellationToken);
        var window = ((App)Application.Current!).CreateMainWindow(Store);
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        Assert.Empty(vm.Events);
        Assert.Equal(vm.Text["NotChecked"], vm.Status);
        window.Close();
        Assert.Equal("broken profile", await File.ReadAllTextAsync(Store.FilePath, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task FailedProfileLoadIsReportedAndCloseDoesNotOverwriteIt()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Store.FilePath, "broken profile", TestContext.Current.CancellationToken);
        var window = ((App)Application.Current!).CreateMainWindow(Store);
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        Assert.StartsWith(vm.Text["OperationFailed"], vm.Status);
        window.Close();
        Assert.Equal("broken profile", await File.ReadAllTextAsync(Store.FilePath, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task MaximizedWindowRetainsNormalBoundsAndMinimizedIsNeverRestored()
    {
        var app = (App)Application.Current!;
        var window = app.CreateMainWindow(Store);
        window.Show();
        window.Width = 1080;
        window.Height = 730;
        window.Position = new PixelPoint(20, 30);
        window.WindowState = WindowState.Maximized;
        window.Close();
        var profile = await Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1080, profile.Window.Width);
        Assert.Equal(730, profile.Window.Height);
        Assert.True(profile.Window.IsMaximized);
        var reopened = app.CreateMainWindow(Store);
        reopened.Show();
        Assert.Equal(WindowState.Maximized, reopened.WindowState);
        reopened.WindowState = WindowState.Minimized;
        reopened.Close();
        var third = app.CreateMainWindow(Store);
        third.Show();
        Assert.Equal(WindowState.Maximized, third.WindowState);
        third.Close();
    }

    [AvaloniaFact]
    public void CancelledCloseDoesNotPersistSettings()
    {
        var window = ((App)Application.Current!).CreateMainWindow(Store);
        window.Show();
        EventHandler<WindowClosingEventArgs> cancel = (_, args) => args.Cancel = true;
        window.Closing += cancel;
        window.Close();
        Assert.True(window.IsVisible);
        Assert.False(File.Exists(Store.FilePath));
        Assert.False(File.Exists(Store.StartupFilePath));
        window.Closing -= cancel;
        window.Close();
        Assert.True(File.Exists(Store.FilePath));
    }

    [AvaloniaFact]
    public async Task OffScreenPositionReturnsToAvailableDisplay()
    {
        await Store.SaveAsync(new() { Window = new() { X = 90000, Y = 90000, Width = 1100, Height = 740 } },
            TestContext.Current.CancellationToken);
        var window = ((App)Application.Current!).CreateMainWindow(Store);
        window.Show();
        Assert.NotNull(window.Screens.ScreenFromPoint(window.Position));
        Assert.NotEqual(new PixelPoint(90000, 90000), window.Position);
        window.Close();
    }

    [AvaloniaFact]
    public void OverviewShowsGenericAgentApplicationNameAndSettingsToggle()
    {
        var window = ((App)Application.Current!).CreateMainWindow(Store);
        window.Show();
        try
        {
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "OpenAI-compatible client");
            Assert.NotNull(window.FindControl<ToggleSwitch>("RememberSettingsToggle"));
            ((MainWindowViewModel)window.DataContext!).LanguageIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "OpenAI-compatible client");
            Assert.Equal("Remember settings between launches", Avalonia.Automation.AutomationProperties.GetName(window.FindControl<ToggleSwitch>("RememberSettingsToggle")!));
        }
        finally { window.Close(); }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
