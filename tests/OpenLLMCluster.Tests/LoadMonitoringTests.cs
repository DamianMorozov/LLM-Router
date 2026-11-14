// Проверяет системные измерения, отдельные компьютеры, недоступные узлы и отсутствие устаревших чисел после потери связи
namespace OpenLLMCluster.Tests;

public sealed class LoadMonitoringTests
{
    [Fact]
    public void CpuCountersDistinguishActualZeroFromMissingIntervalAndAvoidGuestDoubleCount()
    {
        Assert.Null(CpuLoadProbe.Calculate(10, 100, 10, 100));
        Assert.Null(CpuLoadProbe.Calculate(10, 100, 5, 20));
        Assert.Equal(75d, CpuLoadProbe.Calculate(10, 100, 35, 200));
        Assert.Equal(0d, CpuLoadProbe.Calculate(10, 100, 110, 200));
        var counters = CpuLoadProbe.ParseLinux("cpu 100 20 30 400 50 6 7 8 90 5\ncpu0 0 0 0 0\n");
        Assert.Equal(450UL, counters.Idle);
        Assert.Equal(621UL, counters.Total);
    }

    [Fact]
    public void NvidiaParserKeepsDeviceIdentityMemoryAndUnavailableUtilization()
    {
        var gpus = EnvironmentProbe.ParseNvidia("0, Test GPU A, 8192 MiB, 4096 MiB, 73 %, 610.0\n1, Test GPU B, 16384 MiB, 1024 MiB, N/A, 610.0\n");
        Assert.Equal(2, gpus.Count);
        Assert.Equal("Test GPU A", gpus[0].Name);
        Assert.Equal(73, gpus[0].UtilizationPercent);
        Assert.Equal(4096, gpus[0].MemoryUsedMiB);
        Assert.Equal(8192, gpus[0].MemoryTotalMiB);
        Assert.Null(gpus[1].UtilizationPercent);
        Assert.Equal(1, gpus[1].Index);
        Assert.Empty(EnvironmentProbe.ParseNvidia("invalid output"));
    }

    [Fact]
    public async Task LiveSystemCpuBecomesAvailableAfterSecondInterval()
    {
        var probe = new CpuLoadProbe();
        await probe.ReadAsync(TestContext.Current.CancellationToken);
        await Task.Delay(120, TestContext.Current.CancellationToken);
        var load = await probe.ReadAsync(TestContext.Current.CancellationToken);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        { Assert.NotNull(load); Assert.InRange(load.Value, 0, 100); }
    }

    [Theory]
    [InlineData("slow")]
    [InlineData("frameworks")]
    public async Task MacCpuReturnsSecondSampleOnSlowHostWithoutUnneededMemoryCollection(string mode)
    {
        var marker = Path.Combine(Path.GetTempPath(), "olc-top-" + Guid.NewGuid().ToString("N"));
        var probe = new CpuLoadProbe(CreateMacRunner(mode, marker));
        try
        {
            Assert.Equal(25d, await ReadMacAsync(probe, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public async Task MacCpuCancellationStopsItsChildProcess()
    {
        var marker = Path.Combine(Path.GetTempPath(), "olc-top-" + Guid.NewGuid().ToString("N"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var read = ReadMacAsync(new CpuLoadProbe(CreateMacRunner("cancel", marker)), cancellation.Token);
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(marker)) await Task.Delay(25, startup.Token);
            using var process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker, startup.Token)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token);
            Assert.True(process.HasExited);
        }
        finally { cancellation.Cancel(); File.Delete(marker); }
    }

    [AvaloniaFact]
    public async Task OverviewShowsEachComputerAndHidesStaleNumbersWhenAgentFails()
    {
        using var vm = new MainWindowViewModel(new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profile.json")));
        vm.LocalLoadReader = _ => Task.FromResult(Snapshot("local-test", 17, 28));
        var remoteCalls = 0;
        vm.RemoteLoadReader = (_, token, _) =>
        {
            Assert.Equal("test-key", token);
            remoteCalls++;
            return Task.FromResult(Snapshot("remote-test", 83, 91));
        };
        vm.Nodes.Add(new(new() { Name = "Worker A", Host = "worker.example" }, vm.Text) { AccessToken = "test-key" });
        vm.Nodes.Add(new(new() { Name = "No key", Host = "locked.example" }, vm.Text));
        vm.Nodes.Add(new(new() { Name = "Disabled", Host = "disabled.example", Enabled = false }, vm.Text));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var button = window.FindControl<Button>("RefreshLoadsButton")!;
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)button.Command!).ExecuteAsync(null);
            Assert.Equal(4, vm.Computers.Count);
            Assert.Equal(17, vm.Computers[0].CpuUsage);
            Assert.Equal(83, vm.Computers[1].CpuUsage);
            Assert.Equal("worker.example:5100", vm.Computers[1].Address);
            Assert.Equal("remote-test", vm.Computers[1].HostName);
            Assert.Equal(91, Assert.Single(vm.Computers[1].Gpus).Load);
            Assert.Equal(50, vm.Computers[1].Gpus[0].MemoryPercent);
            Assert.Equal("MonitorNeedsKey", vm.Computers[2].StatusKey);
            Assert.Equal("MonitorDisabled", vm.Computers[3].StatusKey);
            Assert.Equal(1, remoteCalls);
            vm.RemoteLoadReader = (_, _, _) => throw new IOException("offline");
            await vm.RefreshLoadsCommand.ExecuteAsync(null);
            Assert.False(vm.Computers[1].IsOnline);
            Assert.False(vm.Computers[1].HasCpu);
            Assert.Equal(vm.Text["MeasurementUnavailable"], vm.Computers[1].CpuLabel);
            Assert.NotNull(vm.Computers[1].UpdatedAt);
            Assert.True(vm.Computers[0].IsOnline);
            vm.LanguageIndex = 2;
            Assert.Equal("Offline", vm.Computers[1].Status);
            vm.Nodes.RemoveAt(1);
            await vm.RefreshLoadsCommand.ExecuteAsync(null);
            Assert.Equal(3, vm.Computers.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task OldAgentRequiresUpdateInsteadOfShowingZeroUsage()
    {
        using var vm = new MainWindowViewModel(new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profile.json")));
        vm.LocalLoadReader = _ => Task.FromResult(new NodeSnapshot { HostName = "old-agent" });
        await vm.RefreshLoadsCommand.ExecuteAsync(null);
        Assert.Equal("MonitorUpdateAgent", vm.Computers[0].StatusKey);
        Assert.False(vm.Computers[0].HasCpu);
        Assert.Equal(vm.Text["MeasurementUnavailable"], vm.Computers[0].CpuLabel);
    }

    // Подменяет только исполняемый файл top отдельным процессом, сохраняя реальные аргументы сборщика
    private static EngineCommandRunner CreateMacRunner(string mode, string marker) => new(plan =>
    {
        Assert.Equal("/usr/bin/top", plan.Executable);
        Assert.Equal(new[] { "-l", "2", "-s", "1", "-n", "0" }, plan.Arguments.Take(6));
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OpenLLMCluster.EngineFixture", "bin",
            new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "OpenLLMCluster.EngineFixture.dll"));
        return EngineCommandRunner.CreateStartInfo(new("dotnet", [fixture, "--fixture-top", mode, marker, .. plan.Arguments]));
    });

    // Позволяет проверить ветку macOS с настоящим дочерним процессом на Windows и Linux
    private static Task<double?> ReadMacAsync(CpuLoadProbe probe, CancellationToken ct)
        => (Task<double?>)typeof(CpuLoadProbe).GetMethod("ReadMacAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(probe, [ct])!;

    // Создаёт явно тестовый снимок, не подставляемый в рабочую диагностику
    private static NodeSnapshot Snapshot(string host, double cpu, double gpu) => new()
    {
        HostName = host, CpuUsagePercent = cpu, TelemetryAvailable = true, SampledAt = DateTimeOffset.UtcNow,
        Gpus = [new() { Index = 0, Name = "Fixture GPU", UtilizationPercent = gpu, MemoryTotalMiB = 8192, MemoryUsedMiB = 4096 }]
    };
}
