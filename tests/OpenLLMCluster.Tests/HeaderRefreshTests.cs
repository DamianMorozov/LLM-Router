// Проверяет перенос роли в верхнюю панель и удаление ручного сохранения до изменения интерфейса
namespace OpenLLMCluster.Tests;

public sealed class HeaderRefreshTests
{
    [AvaloniaFact]
    public void RoleToggleReplacesSelectorAndSaveButtonInHeader()
    {
        using var vm = new MainWindowViewModel(new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profile.json")));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, vm.Text["Save"]));
            Assert.Null(window.FindControl<ComboBox>("RoleSelector"));
            var toggle = window.FindControl<ToggleSwitch>("RoleToggle");
            Assert.NotNull(toggle);
            window.UpdateLayout();
            var caption = Assert.Single(toggle.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == vm.Text["CoordinatorRole"]);
            var bottom = caption.TranslatePoint(new Point(0, caption.Bounds.Height), toggle);
            Assert.NotNull(bottom);
            Assert.InRange(bottom.Value.Y, 0, toggle.Bounds.Height + 0.5);
            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((int)NodeRole.Worker, vm.RoleIndex);
            vm.RoleIndex = (int)NodeRole.Coordinator;
            Dispatcher.UIThread.RunJobs();
            Assert.True(toggle.IsChecked);
            Assert.Equal(window.FindControl<ComboBox>("ThemeSelector")!.Parent, toggle.Parent);
        }
        finally { window.Close(); }
    }
}
