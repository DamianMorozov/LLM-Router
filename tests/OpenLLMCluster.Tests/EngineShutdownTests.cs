// Воспроизводит закрытие владельца непосредственно перед созданием процесса модели
namespace OpenLLMCluster.Tests;

public sealed class EngineShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrStoppedCreationDoesNotHideErrorOrLeaveOwnedProcess(bool stopBeforeStart)
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        var original = EngineProcessTests.CreateRunner();
        EngineProcessHost? host = null;
        var runner = new EngineCommandRunner(plan =>
        {
            var result = original.StartFactory(plan);
            if (plan.Arguments.Contains("--port"))
            {
                if (stopBeforeStart) host!.Stop();
                else result.FileName = Path.Combine(profile.RuntimeDirectory, "missing-fixture-executable");
            }
            return result;
        });
        using var owner = host = new EngineProcessHost(runner);
        var error = await Record.ExceptionAsync(() => owner.StartAsync(profile, "fixture-key", TestContext.Current.CancellationToken));
        if (stopBeforeStart) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<System.ComponentModel.Win32Exception>(error);
        Assert.False(owner.IsRunning);
    }

    [Fact]
    public async Task DisposedOwnerCannotStartModelAfterProcessFactoryReturns()
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        var original = EngineProcessTests.CreateRunner();
        EngineProcessHost? host = null;
        var runner = new EngineCommandRunner(plan =>
        {
            var result = original.StartFactory(plan);
            if (plan.Arguments.Contains("--port")) host!.Dispose();
            return result;
        });
        using var owner = host = new EngineProcessHost(runner);
        try
        {
            var error = await Record.ExceptionAsync(() => owner.StartAsync(profile, "fixture-key", TestContext.Current.CancellationToken));
            Assert.IsType<ObjectDisposedException>(error);
            Assert.False(owner.IsRunning);
            Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.ApiPort, TestContext.Current.CancellationToken)).IsOpen);
        }
        finally { owner.Stop(); }
    }
}
