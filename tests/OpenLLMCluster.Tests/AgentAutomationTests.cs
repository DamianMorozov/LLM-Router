// Проверяет реальный дочерний агент, TCP-порты, авторизацию, очистку процессов и границы правила брандмауэра
namespace OpenLLMCluster.Tests;

public sealed class AgentAutomationTests
{
    public static bool IsNonWindows => !OperatingSystem.IsWindows();
    [Fact]
    public async Task PortProbeDistinguishesOpenAndClosedPorts()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probe = new NetworkPortProbe();
        Assert.True((await probe.CheckAsync("127.0.0.1", port, TestContext.Current.CancellationToken)).IsOpen);
        listener.Stop();
        Assert.False((await probe.CheckAsync("127.0.0.1", port, TestContext.Current.CancellationToken)).IsOpen);
        await Assert.ThrowsAsync<ArgumentException>(() => probe.CheckAsync("http://invalid-host/", port, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OwnedAgentStartsAuthenticatesAndStopsWithoutLeakingPort()
    {
        var port = FreePort();
        using var host = new LocalAgentHost(typeof(Agent.Program).Assembly.Location);
        var token = LocalAgentHost.GenerateToken();
        var snapshot = await host.StartAsync(new() { Port = port }, token, TestContext.Current.CancellationToken);
        Assert.True(host.IsRunning);
        Assert.Equal(Environment.MachineName, snapshot.HostName);
        var node = new NodeEndpoint { Name = "local", Host = "127.0.0.1", AgentPort = port, RpcPort = port == 50052 ? 50053 : 50052 };
        var error = await Assert.ThrowsAsync<RpcException>(() => new NodeAgentClient().ProbeAsync(node, "wrong", TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        Assert.True((await new NetworkPortProbe().CheckAsync(node.Host, port, TestContext.Current.CancellationToken)).IsOpen);
        host.Stop();
        Assert.False(host.IsRunning);
        Assert.False((await new NetworkPortProbe().CheckAsync(node.Host, port, TestContext.Current.CancellationToken)).IsOpen);
    }

    [Fact]
    public async Task ClosingViewModelStopsOwnedProcessAndDoesNotPersistToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), "open-llm-cluster-agent-" + Guid.NewGuid().ToString("N"));
        var store = new ProfileStore(Path.Combine(directory, "profile.json"));
        var host = new LocalAgentHost(typeof(Agent.Program).Assembly.Location);
        var vm = new MainWindowViewModel(store, host) { LocalAgentPort = FreePort() };
        vm.GenerateAgentTokenCommand.Execute(null);
        var token = vm.LocalAgentToken;
        try
        {
            await vm.StartLocalAgentCommand.ExecuteAsync(null);
            Assert.True(vm.IsLocalAgentRunning, vm.LocalAgentStatus);
            Assert.False(vm.CanEditLocalAgent);
            Assert.False(vm.StartLocalAgentCommand.CanExecute(null));
            Assert.True(vm.StopLocalAgentCommand.CanExecute(null));
            await vm.CheckLocalAgentPortCommand.ExecuteAsync(null);
            Assert.StartsWith(vm.Text["TcpPortOpen"], vm.LocalAgentStatus);
            vm.SaveOnClose();
            var json = await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(token, json);
            Assert.DoesNotContain("LocalAgentToken", json);
            Assert.Equal(vm.LocalAgentPort, store.Load().Agent.Port);
            Assert.DoesNotContain(token, string.Join('\n', vm.Events));
            vm.Dispose();
            Assert.False(host.IsRunning);
            Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", vm.LocalAgentPort, TestContext.Current.CancellationToken)).IsOpen);
        }
        finally { vm.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedStartOnOccupiedPortLeavesOtherListenerRunning()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var host = new LocalAgentHost(typeof(Agent.Program).Assembly.Location);
        await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(new() { Port = port }, "", TestContext.Current.CancellationToken));
        Assert.False(host.IsRunning);
        Assert.True((await new NetworkPortProbe().CheckAsync("127.0.0.1", port, TestContext.Current.CancellationToken)).IsOpen);
    }

    [Fact]
    public async Task LanRequiresOptInAndStrongTokenAndKeysAreUnique()
    {
        var first = LocalAgentHost.GenerateToken();
        var second = LocalAgentHost.GenerateToken();
        Assert.Equal(64, first.Length);
        Assert.NotEqual(first, second);
        using var host = new LocalAgentHost();
        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(new() { ListenAddress = "0.0.0.0" }, first,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(new() { ListenAddress = "0.0.0.0", AllowLan = true }, "short",
            TestContext.Current.CancellationToken));
        Assert.False(host.IsRunning);
    }

    [Theory(SkipWhen = nameof(IsNonWindows), Skip = "Проверка Windows PowerShell выполняется только на Windows")]
    [InlineData(false, 5100, "Open-LLM-Cluster-Agent-TCP-5100")]
    [InlineData(true, 50052, "Open-LLM-Cluster-RPC-TCP-50052")]
    public async Task FirewallScriptCreatesAndUpdatesScopedRuleWithRealPowerShellBinding(bool rpc, int port, string name)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsFirewall.BuildRuleScript(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsFirewall.BuildRuleScript(65536));
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "FirewallHarness.ps1"),
            TestContext.Current.CancellationToken);
        var script = fixture.Replace("__APPLY_RULE__", WindowsFirewall.BuildRuleScript(port, rpc: rpc))
            .Replace("Open-LLM-Cluster-Agent-TCP-5100", name).Replace("5100", port.ToString());
        var path = Path.Combine(Path.GetTempPath(), "open-llm-cluster-firewall-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            await File.WriteAllTextAsync(path, script, TestContext.Current.CancellationToken);
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await error);
            Assert.Contains("RULE_POLICY_OK", await output);
        }
        finally { File.Delete(path); }
    }

    // Получает свободный локальный порт для собственного дочернего процесса
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
