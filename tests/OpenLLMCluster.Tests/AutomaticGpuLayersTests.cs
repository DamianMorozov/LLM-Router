// Проверяет автоматическое размещение весов без фиксированных 99 слоёв и пропорций, ручной режим и сохранение настроек
namespace OpenLLMCluster.Tests;

public sealed class AutomaticGpuLayersTests
{
    [Fact]
    public void AutomaticPlacementLeavesLayersAndSplitToFitWhilePreservingRequestedContextAndDevices()
    {
        var profile = new RouterProfile { GpuLayers = -1, ContextLength = 131072,
            ModelSettings = new() { Devices = "CUDA0,RPC0", TensorSplit = "2,1", Parallel = 8, DeviceReserveMiB = 1536 } };
        var args = new RuntimePlanner().Build(profile).Arguments;
        Assert.Equal("auto", args[Array.IndexOf(args, "--n-gpu-layers") + 1]);
        Assert.Equal("on", args[Array.IndexOf(args, "--fit") + 1]);
        Assert.Equal("1536", args[Array.IndexOf(args, "--fit-target") + 1]);
        Assert.DoesNotContain("--tensor-split", args);
        Assert.Equal("CUDA0,RPC0", args[Array.IndexOf(args, "--device") + 1]);
        Assert.Equal("131072", args[Array.IndexOf(args, "--ctx-size") + 1]);
        Assert.Equal("8", args[Array.IndexOf(args, "--parallel") + 1]);
        Assert.Equal("2,1", profile.ModelSettings.TensorSplit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(99)]
    public void ExplicitLayerCountAndSplitArePreserved(int layers)
    {
        var profile = new RouterProfile { GpuLayers = layers, ModelSettings = new() { Devices = "CUDA0,RPC0", TensorSplit = "2,1" } };
        var args = new RuntimePlanner().Build(profile).Arguments;
        Assert.Equal(layers.ToString(), args[Array.IndexOf(args, "--n-gpu-layers") + 1]);
        Assert.Equal("2,1", args[Array.IndexOf(args, "--tensor-split") + 1]);
    }

    [Fact]
    public void NewProfilesDoNotForceNinetyNineLayersAndInvalidNegativeValuesAreRejected()
    {
        Assert.Equal(-1, new RouterProfile().GpuLayers);
        Assert.Throws<ArgumentException>(() => new RuntimePlanner().Build(new() { GpuLayers = -2 }));
    }

    [AvaloniaFact]
    public void AutomaticLayersCanBeSelectedThroughActualEditorAndRestored()
    {
        var directory = Path.Combine(Path.GetTempPath(), "olc-auto-layers-" + Guid.NewGuid().ToString("N"));
        var store = new ProfileStore(Path.Combine(directory, "profile.json"));
        using var vm = new MainWindowViewModel(store) { GpuLayers = 99 };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var editor = window.FindControl<NumericUpDown>("GpuLayersEditor")!;
            Assert.Equal(-1, editor.Minimum);
            editor.Value = -1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(-1, vm.GpuLayers);
            vm.SaveOnClose();
            using var restored = new MainWindowViewModel(store);
            restored.Initialize();
            Assert.Equal(-1, restored.GpuLayers);
        }
        finally { window.Close(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
