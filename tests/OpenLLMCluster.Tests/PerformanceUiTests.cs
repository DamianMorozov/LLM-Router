// Проверяет новые сведения RAM, копирование Model ID, применение профиля и общую строку заголовка
namespace OpenLLMCluster.Tests;

public sealed class PerformanceUiTests
{
    [Fact]
    public void SnapshotCarriesPhysicalMemorySeparatelyFromManagedBudget()
    {
        Assert.NotNull(typeof(NodeSnapshot).GetProperty("MemoryTotalBytes"));
        Assert.NotNull(typeof(NodeSnapshot).GetProperty("MemoryAvailableBytes"));
    }

    [AvaloniaFact]
    public async Task ModelIdCanBeCopiedWithoutLoadingAWeightFile()
    {
        using var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var label = window.FindControl<SelectableTextBlock>("ModelIdValue");
            Assert.NotNull(label);
            Assert.Equal("open-llm-cluster", label.Text);
            var button = window.FindControl<Button>("CopyModelIdButton")!;
            var command = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(button.Command);
            await command.ExecuteAsync(button.CommandParameter);
            Assert.Equal(label.Text, await window.Clipboard!.TryGetTextAsync());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InteractiveProfileChangesActualLaunchSettingsAndIsBlockedWhileRunning()
    {
        using var vm = CreateViewModel();
        vm.ContextLength = 128000;
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("RuntimeTab");
            var selector = window.FindControl<ComboBox>("WorkloadSelector");
            Assert.NotNull(selector);
            selector.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var apply = window.FindControl<Button>("ApplyWorkloadButton")!;
            Assert.True(apply.Command!.CanExecute(null));
            apply.Command.Execute(null);
            Assert.Equal(8192, vm.ContextLength);
            Assert.Equal("f16", vm.CacheTypeK);
            Assert.Equal(1, vm.ParallelRequests);
            vm.IsEngineRunning = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(apply.Command.CanExecute(null));
            vm.IsEngineRunning = false;
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(980, 1)]
    [InlineData(1240, 1)]
    [InlineData(980, 2)]
    [InlineData(1240, 2)]
    public void HeaderControlsShareOneVerticalCenter(int width, int language)
    {
        using var vm = CreateViewModel();
        vm.LanguageIndex = language;
        var window = new MainWindow { DataContext = vm, Width = width };
        window.Show();
        try
        {
            window.UpdateLayout();
            var title = window.FindControl<TextBlock>("ApplicationNameText")!;
            Assert.Null(window.FindControl<TextBlock>("ApplicationVersionText"));
            var center = title.TranslatePoint(new Point(0, title.Bounds.Height / 2), window)!.Value.Y;
            foreach (var name in new[] { "LanguageSelector", "ThemeSelector", "RoleToggle", "RememberSettingsToggle", "RememberAgentKeysToggle" })
            {
                var control = window.FindControl<Control>(name)!;
                var position = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height / 2), window)!.Value;
                Assert.InRange(Math.Abs(position.Y - center), 0, 3);
                Assert.InRange(position.X, 0, width - 20);
                if (control is ToggleSwitch toggle)
                {
                    var track = toggle.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SwitchKnobBounds");
                    var trackCenter = track.TranslatePoint(new Point(0, track.Bounds.Height / 2), window)!.Value.Y;
                    Assert.InRange(Math.Abs(trackCenter - center), 0, 1);
                }
            }
            Assert.Null(window.FindControl<Border>("OverviewSettingsCard"));
        }
        finally { window.Close(); }
    }

    // Использует отдельное хранилище, не читая и не изменяя настройки работающего приложения
    private static MainWindowViewModel CreateViewModel() => new(new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profile.json")));
}
