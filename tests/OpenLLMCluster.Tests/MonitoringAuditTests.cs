// Проверяет, что смена адреса узла не переносит показатели прежнего ПК, а N/A VRAM не превращается в свободную память
namespace OpenLLMCluster.Tests;

public sealed class MonitoringAuditTests
{
    [AvaloniaFact]
    public async Task ChangedEndpointDiscardsInFlightOldComputerSnapshot()
    {
        using var vm = new MainWindowViewModel(new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profile.json")));
        vm.LocalLoadReader = _ => Task.FromResult(new NodeSnapshot { TelemetryAvailable = true });
        var pending = new TaskCompletionSource<NodeSnapshot>();
        vm.RemoteLoadReader = (_, _, _) => pending.Task;
        var node = new NodeViewModel(new() { Name = "worker", Host = "old.example" }, vm.Text) { AccessToken = "test-key" };
        vm.Nodes.Add(node);
        var refresh = vm.RefreshLoadsCommand.ExecuteAsync(null);
        node.Host = "new.example";
        pending.SetResult(new() { HostName = "old-machine", CpuUsagePercent = 99, TelemetryAvailable = true });
        await refresh;
        Assert.False(vm.Computers[1].IsOnline);
        Assert.False(vm.Computers[1].HasCpu);
    }

    [Fact]
    public void MissingUsedMemoryIsNotReportedAsZeroUsed()
    {
        var gpu = Assert.Single(EnvironmentProbe.ParseNvidia("0, Test GPU, 8192 MiB, N/A, 10 %, 610.0"));
        var view = new GpuLoadViewModel(gpu, new Localizer());
        Assert.Equal(new Localizer()["MeasurementUnavailable"], view.MemoryLabel);
    }
}
