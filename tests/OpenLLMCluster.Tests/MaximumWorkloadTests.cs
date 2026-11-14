// Проверяет применение максимального контекста через UI, реальные CLI-аргументы и сохранение профиля
namespace OpenLLMCluster.Tests;

public sealed class MaximumWorkloadTests
{
    [AvaloniaTheory]
    [InlineData(1, "Максимальный контекст")]
    [InlineData(2, "Maximum context")]
    public void MaximumProfileAppliesThroughUiAndPersists(int language, string caption)
    {
        var directory = Path.Combine(Path.GetTempPath(), "olc-maximum-" + Guid.NewGuid().ToString("N"));
        var store = new ProfileStore(Path.Combine(directory, "profile.json"));
        using var vm = new MainWindowViewModel(store) { LanguageIndex = language, RememberAgentKeys = false };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("RuntimeTab");
            var selector = window.FindControl<ComboBox>("WorkloadSelector")!;
            Assert.Equal(5, selector.ItemCount);
            vm.ModelPath = "models/demo.gguf";
            vm.MaxOutputTokens = 2048;
            selector.SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(caption, vm.WorkloadOptions[4].Label);
            Assert.Contains("131072", vm.WorkloadSummary);
            Assert.Contains("32768", vm.WorkloadSummary);
            Assert.Equal(2048, vm.MaxOutputTokens);
            var apply = window.FindControl<Button>("ApplyWorkloadButton")!;
            Assert.True(apply.Command!.CanExecute(null));
            apply.Command.Execute(null);
            Assert.Equal(131072, vm.ContextLength);
            Assert.Equal(32768, vm.MaxOutputTokens);
            Assert.Equal(1, vm.ParallelRequests);
            Assert.Equal("f16", vm.CacheTypeK);
            Assert.Equal("f16", vm.CacheTypeV);
            Assert.True(vm.FlashAttention);
            vm.SaveOnClose();
            var saved = store.Load();
            var args = new RuntimePlanner().Build(saved).Arguments;
            Assert.Equal("131072", args[Array.IndexOf(args, "--ctx-size") + 1]);
            Assert.Equal("32768", args[Array.IndexOf(args, "--predict") + 1]);
            using var restored = new MainWindowViewModel(store);
            restored.Initialize();
            Assert.Equal(4, restored.WorkloadIndex);
            Assert.Equal(131072, restored.ContextLength);
            Assert.Equal(32768, restored.MaxOutputTokens);
            vm.IsEngineRunning = true;
            Assert.False(apply.Command.CanExecute(null));
            vm.IsEngineRunning = false;
            vm.RoleIndex = 1;
            Assert.False(apply.Command.CanExecute(null));
            vm.RoleIndex = 0;
            vm.MaxOutputTokens = 8192;
            Assert.Equal(0, vm.WorkloadIndex);
            vm.SaveOnClose();
            Assert.Equal(WorkloadMode.Custom, store.Load().Workload);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
