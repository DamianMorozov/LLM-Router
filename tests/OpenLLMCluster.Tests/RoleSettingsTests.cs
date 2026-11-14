// Проверяет версию только в заголовке, ограничения выбранной роли и сохранность заблокированных настроек
namespace OpenLLMCluster.Tests;

public sealed class RoleSettingsTests
{
    [AvaloniaFact]
    public void VersionOnlyAppearsInNativeWindowTitleWithPrefix()
    {
        using var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Assert.Equal("Open LLM Cluster v" + ApplicationInfo.Version, window.Title);
            Assert.Null(window.FindControl<TextBlock>("ApplicationVersionText"));
            Assert.DoesNotContain(window.FindControl<Grid>("HeaderSettings")!.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == ApplicationInfo.Version);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void RoleSwitchLocksOnlyInapplicableEditorsAndRestoresTheirValues(int language)
    {
        using var vm = CreateViewModel();
        vm.LanguageIndex = language;
        vm.ModelPath = "saved.gguf";
        vm.ContextLength = 32768;
        vm.ApiPort = 8087;
        vm.RpcPort = 50057;
        vm.AddNodeCommand.Execute(null);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var tabs = window.FindControl<TabControl>("WorkspaceTabs")!;
            tabs.SelectedItem = window.FindControl<TabItem>("RuntimeTab");
            foreach (var coordinator in new[] { true, false, true })
            {
                window.FindControl<ToggleSwitch>("RoleToggle")!.IsChecked = coordinator;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Equal(coordinator, window.FindControl<Button>("FindModelsButton")!.IsEffectivelyEnabled);
                Assert.Equal(coordinator, window.FindControl<NumericUpDown>("ApiPortEditor")!.IsEffectivelyEnabled);
                Assert.Equal(coordinator, window.FindControl<NumericUpDown>("ContextLengthEditor")!.IsEffectivelyEnabled);
                Assert.Equal(coordinator, window.FindControl<NumericUpDown>("CpuThreadsEditor")!.IsEffectivelyEnabled);
                Assert.Equal(coordinator, window.FindControl<NumericUpDown>("GpuLayersEditor")!.IsEffectivelyEnabled);
                Assert.Equal(!coordinator, window.FindControl<NumericUpDown>("RpcPortEditor")!.IsEffectivelyEnabled);
                Assert.True(window.FindControl<Button>("FindRuntimeButton")!.IsEffectivelyEnabled);
                Assert.True(window.FindControl<Button>("InstallLlamaButton")!.IsEffectivelyEnabled);
                Assert.True(window.FindControl<TextBox>("BindAddressEditor")!.IsEffectivelyEnabled);
                Assert.Equal(coordinator, window.FindControl<TabItem>("NodesTab")!.IsEnabled);
                Assert.Equal(coordinator, window.FindControl<Border>("ModelSettingsCard")!.IsEffectivelyEnabled);
                Assert.Equal(coordinator, window.FindControl<ComboBox>("WorkloadSelector")!.IsEffectivelyEnabled);
                Assert.False(vm.CanSelectEffort);
                Assert.Equal(coordinator, vm.FindModelsCommand.CanExecute(null));
                Assert.Equal(coordinator, vm.CheckClusterCommand.CanExecute(null));
                Assert.Equal(coordinator, vm.AddNodeCommand.CanExecute(null));
                Assert.Equal(coordinator, vm.PairNodeCommand.CanExecute(vm.Nodes[0]));
                Assert.Equal(coordinator, vm.CheckNodeAgentPortCommand.CanExecute(vm.Nodes[0]));
                Assert.Equal(coordinator, vm.StartRemoteRpcCommand.CanExecute(vm.Nodes[0]));
                Assert.Equal(!coordinator && OperatingSystem.IsWindows(), vm.EnsureRpcFirewallCommand.CanExecute(null));
                tabs.SelectedItem = window.FindControl<TabItem>("LocalAgentTab");
                Dispatcher.UIThread.RunJobs();
                Assert.True(window.FindControl<TextBox>("LocalAgentAddress")!.IsEffectivelyEnabled);
                tabs.SelectedItem = window.FindControl<TabItem>("RuntimeTab");
            }
            Assert.Equal("saved.gguf", vm.ModelPath);
            Assert.Equal(32768, vm.ContextLength);
            Assert.Equal(8087, vm.ApiPort);
            Assert.Equal(50057, vm.RpcPort);
            Assert.Single(vm.Nodes);
            vm.IsEngineRunning = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<ToggleSwitch>("RoleToggle")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<NumericUpDown>("ApiPortEditor")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<Button>("FindRuntimeButton")!.IsEffectivelyEnabled);
            vm.IsEngineRunning = false;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void WorkerCannotAddOrRemoveNodesThroughDisabledCommands()
    {
        using var vm = CreateViewModel();
        vm.AddNodeCommand.Execute(null);
        vm.RoleIndex = (int)NodeRole.Worker;
        Assert.False(vm.AddNodeCommand.CanExecute(null));
        Assert.False(vm.RemoveNodeCommand.CanExecute(vm.Nodes[0]));
        Assert.False(vm.FindModelsCommand.CanExecute(null));
        Assert.False(vm.SuggestGpuSplitCommand.CanExecute(null));
    }

    // Изолирует настройки тестов от профиля пользователя
    private static MainWindowViewModel CreateViewModel() => new(new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profile.json")));
}
