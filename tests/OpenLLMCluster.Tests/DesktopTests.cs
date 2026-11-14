// Проверяет реальное XAML-окно, переключение локализации и инвалидирование предпросмотра
[assembly: AvaloniaTestApplication(typeof(OpenLLMCluster.Tests.UiTestAppBuilder))]

namespace OpenLLMCluster.Tests;

public static class UiTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class DesktopTests
{
    [AvaloniaFact]
    public void HeaderControlsHaveEqualRenderedHeight()
    {
        var window = new MainWindow { DataContext = CreateViewModel() };
        window.Show();
        try
        {
            window.UpdateLayout();
            var language = window.FindControl<ComboBox>("LanguageSelector")!;
            var theme = window.FindControl<ComboBox>("ThemeSelector")!;
            var save = window.FindControl<ToggleSwitch>("RoleToggle")!;
            Assert.Equal(save.Bounds.Height, language.Bounds.Height);
            Assert.Equal(save.Bounds.Height, theme.Bounds.Height);
            Assert.Equal(save.Bounds.Y, language.Bounds.Y);
            Assert.Equal(save.Bounds.Y, theme.Bounds.Y);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LocalAgentTabExposesNetworkControlsAndActions()
    {
        var window = new MainWindow { DataContext = CreateViewModel() };
        window.Show();
        try
        {
            var tab = window.FindControl<TabItem>("LocalAgentTab");
            Assert.NotNull(tab);
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = tab;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.FindControl<TextBox>("LocalAgentAddress"));
            Assert.NotNull(window.FindControl<Button>("StartLocalAgentButton")!.Command);
            Assert.NotNull(window.FindControl<Button>("CheckLocalAgentPortButton")!.Command);
            Assert.NotNull(window.FindControl<Button>("EnsureFirewallButton")!.Command);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MainWindowHasEmbeddedDesktopIcon()
    {
        var window = new MainWindow();
        Assert.NotNull(window.Icon);
    }

    [AvaloniaFact]
    public void LanguageSelectorUpdatesViewModelAndVisibleTabHeaders()
    {
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var selector = window.FindControl<ComboBox>("LanguageSelector")!;
        selector.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.LanguageIndex);
        Assert.Equal("en", vm.Text.Language);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Coordinator");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "System theme");
        var tabs = window.FindControl<TabControl>("WorkspaceTabs")!;
        Assert.Equal("Network nodes", ((TabItem)tabs.Items[1]!).Header);
        selector.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Сетевые узлы", ((TabItem)tabs.Items[1]!).Header);
        window.Close();
    }

    [AvaloniaFact]
    public void RoleSelectorUpdatesRoleAndNodeCommandsModifyCollection()
    {
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.FindControl<ToggleSwitch>("RoleToggle")!.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, vm.RoleIndex);
        vm.AddNodeCommand.Execute(null);
        Assert.Single(vm.Nodes);
        vm.RemoveNodeCommand.Execute(vm.Nodes[0]);
        Assert.Empty(vm.Nodes);
        window.Close();
    }

    [AvaloniaFact]
    public void ApplicationSupportsAllThemeVariants()
    {
        var app = (App)Application.Current!;
        var window = app.CreateMainWindow(new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profile.json")));
        window.Show();
        var selector = window.FindControl<ComboBox>("ThemeSelector")!;
        selector.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        selector.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
        selector.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        window.Close();
    }

    [AvaloniaFact]
    public void WindowRendersBothThemesAndLanguages()
    {
        var app = (App)Application.Current!;
        var window = app.CreateMainWindow(new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profile.json")));
        var vm = (MainWindowViewModel)window.DataContext!;
        window.Show();
        var directory = Path.Combine(Path.GetTempPath(), "OpenLLMCluster.Tests", "screenshots");
        Directory.CreateDirectory(directory);
        foreach (var language in new[] { 1, 2 })
        foreach (var theme in new[] { 1, 2 })
        {
            vm.LanguageIndex = language;
            vm.ThemeIndex = theme;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame()!;
            var name = $"overview-{(language == 1 ? "ru" : "en")}-{(theme == 1 ? "light" : "dark")}.png";
            frame.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            var agentTabs = window.FindControl<TabControl>("WorkspaceTabs")!;
            agentTabs.SelectedItem = window.FindControl<TabItem>("LocalAgentTab");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var agentFrame = window.CaptureRenderedFrame()!;
            agentFrame.Save(Path.Combine(directory, name.Replace("overview", "agent")), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            agentTabs.SelectedIndex = 0;
        }
        vm.LanguageIndex = 1;
        vm.ThemeIndex = 2;
        vm.AddNodeCommand.Execute(null);
        var tabs = window.FindControl<TabControl>("WorkspaceTabs")!;
        foreach (var tab in new[] { 1, 2 })
        {
            tabs.SelectedIndex = tab;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame()!;
            frame.Save(Path.Combine(directory, tab == 1 ? "nodes-ru-dark.png" : "runtime-ru-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        Assert.True(window.Bounds.Width >= 980);
        window.Close();
        app.ApplyTheme(0);
    }

    [AvaloniaFact]
    public async Task NodeCardButtonsAreBoundToParentViewModelCommands()
    {
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.FindControl<TabControl>("WorkspaceTabs")!.SelectedIndex = 1;
        vm.AddNodeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
        var probe = Assert.Single(buttons, button => Equals(button.Content, vm.Text["CheckAgent"]));
        var probeCommand = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(probe.Command);
        await probeCommand.ExecuteAsync(probe.CommandParameter);
        Assert.StartsWith(vm.Text["ConnectionFailed"], vm.Nodes[0].Status);
        var remove = Assert.Single(buttons, button => Equals(button.Content, vm.Text["Remove"]));
        Assert.NotNull(remove.Command);
        remove.Command.Execute(remove.CommandParameter);
        Assert.Empty(vm.Nodes);
        window.Close();
    }

    [Fact]
    public void LocalizationDictionariesHaveIdenticalNonemptyKeys()
    {
        var assembly = typeof(Localizer).Assembly;
        using var ruStream = assembly.GetManifestResourceStream("OpenLLMCluster.Desktop.Localization.ru.json")!;
        using var enStream = assembly.GetManifestResourceStream("OpenLLMCluster.Desktop.Localization.en.json")!;
        var ru = JsonSerializer.Deserialize<Dictionary<string, string>>(ruStream)!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(enStream)!;
        Assert.Equal(ru.Keys.Order(), en.Keys.Order());
        Assert.All(ru.Values.Concat(en.Values), value => Assert.False(string.IsNullOrWhiteSpace(value)));
    }

    [Fact]
    public void ChangingProfileMustClearPreviousLaunchPreview()
    {
        var vm = CreateViewModel();
        vm.CommandPreview = "previous plan";
        vm.ContextLength = 16384;
        Assert.Empty(vm.CommandPreview);
        vm.CommandPreview = "previous plan";
        vm.RoleIndex = 1;
        Assert.Empty(vm.CommandPreview);
    }

    [Fact]
    public void ChangingNodeMustClearPreviousLaunchPreview()
    {
        var vm = CreateViewModel();
        vm.AddNodeCommand.Execute(null);
        vm.CommandPreview = "previous plan";
        vm.Nodes[0].Host = "changed.example";
        Assert.Empty(vm.CommandPreview);
    }

    // Создаёт модель представления без доступа к пользовательскому профилю
    private static MainWindowViewModel CreateViewModel() => new(new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profile.json")));
}
