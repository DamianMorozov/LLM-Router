// Проверяет реальное расположение подписей справа от переключателей и небольшой промежуток между ними
namespace OpenLLMCluster.Tests;

public sealed class ToggleCaptionTests
{
    [AvaloniaTheory]
    [InlineData(980, 1)]
    [InlineData(980, 2)]
    [InlineData(1240, 1)]
    [InlineData(1240, 2)]
    public void CaptionsStayImmediatelyRightOfSwitchesOnEveryPage(int width, int language)
    {
        using var vm = new MainWindowViewModel(new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profile.json")));
        vm.LanguageIndex = language;
        vm.AddNodeCommand.Execute(null);
        var window = new MainWindow { DataContext = vm, Width = width };
        window.Show();
        try
        {
            var tabs = window.FindControl<TabControl>("WorkspaceTabs")!;
            var visited = new HashSet<ToggleSwitch>();
            foreach (var tab in tabs.Items.Cast<TabItem>())
            {
                tabs.SelectedItem = tab;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                foreach (var toggle in window.GetVisualDescendants().OfType<ToggleSwitch>()
                    .Where(toggle => toggle.Name is not ("RoleToggle" or "EngineReadyIndicator" or "AgentReadyIndicator")))
                {
                    var caption = FindCaption(toggle, vm);
                    var captionStart = caption.TranslatePoint(new Point(0, caption.Bounds.Height / 2), toggle);
                    var track = toggle.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "SwitchKnobBounds");
                    var switchRight = track is null ? new Point(toggle.Bounds.Width, toggle.Bounds.Height / 2)
                        : track.TranslatePoint(new Point(track.Bounds.Width, track.Bounds.Height / 2), toggle)!.Value;
                    Assert.NotNull(captionStart);
                    Assert.InRange(captionStart.Value.X - switchRight.X, 4, 16);
                    Assert.InRange(Math.Abs(captionStart.Value.Y - switchRight.Y), 0, 14);
                    visited.Add(toggle);
                }
            }
            Assert.Equal(8, visited.Count);
        }
        finally { window.Close(); }
    }

    // Находит внешнюю или встроенную подпись по доступному имени и содержимому переключателя
    private static TextBlock FindCaption(ToggleSwitch toggle, MainWindowViewModel vm)
    {
        var label = Avalonia.Automation.AutomationProperties.GetName(toggle);
        if (string.IsNullOrEmpty(label)) label = toggle.Content?.ToString();
        if (toggle.Name == "RememberSettingsToggle") label = vm.Text["RememberSettingsShort"];
        if (toggle.Name == "RememberAgentKeysToggle") label = vm.Text["RememberKeysShort"];
        var own = toggle.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Text == label);
        if (own is not null) return own;
        return toggle.GetVisualParent()!.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == label);
    }
}
