// Проверяет настоящий обмен MagicOnion по HTTP/2 через временный локальный порт
namespace OpenLLMCluster.Tests;

public sealed class NodeAgentTests
{
    [Fact]
    public async Task AgentReturnsLiveEnvironmentThroughMagicOnion()
    {
        await using var app = Agent.Program.Build(["--Router:Port", "0", "--Router:AccessToken", "test-access-token"]);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var node = GetEndpoint(app.Services);
        var snapshot = await new NodeAgentClient().ProbeAsync(node, "test-access-token", TestContext.Current.CancellationToken);
        Assert.Equal(Environment.MachineName, snapshot.HostName);
        Assert.Equal(Environment.ProcessorCount, snapshot.LogicalProcessors);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.OperatingSystem));
        Assert.Equal(1, snapshot.ProtocolVersion);
        Assert.True(snapshot.TelemetryAvailable);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Assert.True(snapshot.MemoryTotalBytes > 0);
            Assert.NotNull(snapshot.MemoryAvailableBytes);
            Assert.InRange(snapshot.MemoryAvailableBytes.Value, 0, snapshot.MemoryTotalBytes!.Value);
        }
        Assert.NotEqual(default, snapshot.SampledAt);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        var next = await new NodeAgentClient().ProbeAsync(node, "test-access-token", TestContext.Current.CancellationToken);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) Assert.InRange(next.CpuUsagePercent!.Value, 0, 100);
        Assert.Equal(snapshot.HostName, next.HostName);
        Assert.True(next.SampledAt > snapshot.SampledAt);
        Assert.All(next.Gpus, gpu => Assert.False(string.IsNullOrEmpty(gpu.Name)));
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AgentRejectsWrongToken()
    {
        await using var app = Agent.Program.Build(["--Router:Port", "0", "--Router:AccessToken", "correct-token"]);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<RpcException>(() => new NodeAgentClient()
            .ProbeAsync(GetEndpoint(app.Services), "wrong-token", TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void LanBindingRequiresExplicitOptInAndToken()
    {
        Assert.Throws<InvalidOperationException>(() => Agent.Program.Build(["--Router:ListenAddress", "0.0.0.0"]));
        Assert.Throws<InvalidOperationException>(() => Agent.Program.Build(["--Router:ListenAddress", "0.0.0.0", "--Router:AllowInsecureLan", "true"]));
    }

    // Получает реально выделенный Kestrel порт, не резервируя фиксированный адрес
    private static NodeEndpoint GetEndpoint(IServiceProvider services)
    {
        var address = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var uri = new Uri(address);
        return new() { Name = "test agent", Host = uri.Host, AgentPort = uri.Port, RpcPort = uri.Port == 50052 ? 50053 : 50052 };
    }
}
