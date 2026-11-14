// Проверяет системный язык по умолчанию, смысл переключателя роли и действия компактных журналов
namespace OpenLLMCluster.Tests;

public sealed class CompactDesktopTests
{
    [AvaloniaFact]
    public void WindowAndVisibleHeaderShowActualApplicationVersion()
    {
        var version = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
        using var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Assert.Equal("Open LLM Cluster v" + version, window.Title);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Open LLM Cluster");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "OLC");
            Assert.NotNull(window.Icon);
            Assert.Null(window.FindControl<TextBlock>("ApplicationVersionText"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RunningAgentStillAllowsCopyingAndShowingKeyWhileEditingIsLocked()
    {
        using var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("LocalAgentTab");
            vm.IsLocalAgentRunning = true;
            Dispatcher.UIThread.RunJobs();
            var copy = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, vm.Text["CopyAgentToken"]));
            var show = window.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, vm.Text["ShowAgentToken"]));
            Assert.True(copy.IsEffectivelyEnabled);
            Assert.True(show.IsEffectivelyEnabled);
            Assert.False(window.FindControl<TextBox>("LocalAgentAddress")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<Button>("GenerateAgentTokenButton")!.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FirstLanguageFollowsSystemAndCheckedRoleMeansCoordinator()
    {
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        using var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            var selector = window.FindControl<ComboBox>("LanguageSelector")!;
            Assert.Equal(vm.Text["LanguageSystem"], ((ComboBoxItem)selector.Items[0]!).Content);
            Assert.Equal(0, selector.SelectedIndex);
            Assert.Equal("en", vm.Text.Language);
            Assert.Equal("system", new RouterProfile().Language);
            var role = window.FindControl<ToggleSwitch>("RoleToggle")!;
            Assert.True(role.IsChecked);
            role.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((int)NodeRole.Worker, vm.RoleIndex);
            vm.RoleIndex = (int)NodeRole.Coordinator;
            Dispatcher.UIThread.RunJobs();
            Assert.True(role.IsChecked);
        }
        finally { window.Close(); System.Globalization.CultureInfo.CurrentUICulture = original; }
    }

    [AvaloniaFact]
    public async Task EngineToolbarCopiesMaskedEngineLogAndClearsOnlyEngineLog()
    {
        using var vm = CreateViewModel();
        vm.LocalAgentToken = "agent-secret";
        vm.ServerApiKey = "server-secret";
        vm.AddNodeCommand.Execute(null);
        vm.Nodes[0].AccessToken = "node-secret";
        vm.Events.Add("general event");
        vm.Checks.Add("diagnostic");
        vm.EngineEvents.Add("engine agent-secret server-secret node-secret");
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("RuntimeTab");
            Dispatcher.UIThread.RunJobs();
            var copy = window.FindControl<Button>("CopyEngineLogButton");
            Assert.NotNull(copy);
            var command = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(copy.Command);
            await command.ExecuteAsync(copy.CommandParameter);
            Assert.Equal("engine [token] [token] [token]", await window.Clipboard!.TryGetTextAsync());
            var clear = window.FindControl<Button>("ClearEngineLogButton")!;
            Assert.NotNull(clear.Command);
            clear.Command.Execute(clear.CommandParameter);
            Assert.Empty(vm.EngineEvents);
            Assert.Equal("general event", Assert.Single(vm.Events));
            Assert.Equal("diagnostic", Assert.Single(vm.Checks));
            await command.ExecuteAsync(copy.CommandParameter);
            Assert.Equal("", await window.Clipboard!.TryGetTextAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(980, 1)]
    [InlineData(1240, 2)]
    public void CompactCardsAndJournalActionsFitAvailableWidth(int width, int language)
    {
        using var vm = CreateViewModel();
        vm.LanguageIndex = language;
        var window = new MainWindow { DataContext = vm, Width = width, Height = 880 };
        window.Show();
        try
        {
            window.UpdateLayout();
            var settings = window.FindControl<Grid>("HeaderSettings");
            Assert.NotNull(settings);
            Assert.InRange(settings.Bounds.Height, 35, 55);
            Assert.Contains(window.FindControl<ToggleSwitch>("RememberSettingsToggle")!, settings.GetVisualDescendants());
            var tabs = window.FindControl<TabControl>("WorkspaceTabs")!;
            tabs.SelectedItem = window.FindControl<TabItem>("LocalAgentTab");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(language == 1 ? "Агент" : "Agent", window.FindControl<TabItem>("LocalAgentTab")!.Header);
            var agent = window.FindControl<Border>("AgentNetworkCard")!;
            Assert.InRange(agent.Bounds.Height, 100, 420);
            var firewall = window.FindControl<Button>("EnsureFirewallButton")!.GetVisualAncestors().OfType<Border>()
                .First(border => border.Classes.Contains("card"));
            Assert.InRange(firewall.Bounds.Height, 35, 110);
            tabs.SelectedItem = window.FindControl<TabItem>("JournalTab");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var toolbar = window.FindControl<Grid>("JournalToolbar")!;
            var copy = window.FindControl<Button>("CopyJournalButton")!;
            var clear = window.FindControl<Button>("ClearJournalButton")!;
            Assert.Equal(32, copy.Bounds.Height);
            Assert.Equal(32, clear.Bounds.Height);
            Assert.Equal(copy.Bounds.Y, clear.Bounds.Y);
            var right = clear.TranslatePoint(new Point(clear.Bounds.Width, 0), toolbar);
            Assert.InRange(right!.Value.X, toolbar.Bounds.Width - 1, toolbar.Bounds.Width + 1);
            var profilePath = window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(block => block.Text == vm.ProfilePath);
            var pathRight = profilePath.TranslatePoint(new Point(profilePath.Bounds.Width, 0), window);
            Assert.InRange(pathRight!.Value.X, 0, window.Bounds.Width - 27);
        }
        finally { window.Close(); }
    }

    // Создаёт отдельный профиль, не затрагивая настройки запущенной программы
    private static MainWindowViewModel CreateViewModel()
        => new(new ProfileStore(Path.Combine(Path.GetTempPath(), "open-llm-cluster-compact-" + Guid.NewGuid().ToString("N"), "profile.json")));
}
