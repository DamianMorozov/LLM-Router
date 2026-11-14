// Проверяет поле локальных весов узла, выбор файла, сохранение пути и блокировку по роли и состоянию агента
namespace OpenLLMCluster.Tests;

public sealed class LocalModelDesktopTests
{
    [AvaloniaFact]
    public void LocalModelPathIsAvailableOnAgentPage()
    {
        using var vm = new MainWindowViewModel(new ProfileStore(Path.Combine(Path.GetTempPath(), "olc-model-ui-" + Guid.NewGuid() + ".json")));
        vm.RoleIndex = 1;
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("LocalAgentTab");
            var editor = window.FindControl<TextBox>("LocalModelPathEditor");
            Assert.NotNull(editor);
            Assert.NotNull(window.FindControl<Button>("BrowseLocalModelButton"));
            Assert.NotNull(window.FindControl<Button>("PrepareLocalModelButton"));
            Assert.True(editor.IsEffectivelyEnabled);
            editor.Text = Path.Combine(Path.GetTempPath(), "models", "node.gguf");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(editor.Text, vm.LocalModelPath);
            vm.IsLocalAgentRunning = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.IsEffectivelyEnabled);
            Assert.False(vm.PrepareLocalModelCommand.CanExecute(null));
            vm.IsLocalAgentRunning = false;
            vm.RoleIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.IsEffectivelyEnabled);
            vm.RoleIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LocalPathIsSavedWithAgentSettingsAndNotLoadedWhenRememberingIsDisabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "olc-local-path-" + Guid.NewGuid().ToString("N"));
        var store = new ProfileStore(Path.Combine(directory, "profile.json"));
        try
        {
            using (var vm = new MainWindowViewModel(store) { LocalModelPath = Path.Combine(directory, "weights.gguf") }) vm.SaveOnClose();
            using var restored = new MainWindowViewModel(store);
            restored.Initialize();
            Assert.Equal(Path.Combine(directory, "weights.gguf"), restored.LocalModelPath);
            store.SaveAutomaticSettings(enabled: false);
            using var disabled = new MainWindowViewModel(store);
            disabled.Initialize();
            Assert.Equal("", disabled.LocalModelPath);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
