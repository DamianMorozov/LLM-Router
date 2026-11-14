// Проверяет состояния сервиса, исключение параллельных проверок и работу без установленного пакета
namespace OpenLLMCluster.Tests;

public sealed class ApplicationUpdateTests
{
    [Fact]
    public async Task UnpackagedAppShowsReleasesLinkWithoutCheckingNetwork()
    {
        var adapter = new FakeAdapter { IsInstalled = false };
        var service = new ApplicationUpdateService(() => adapter);
        Assert.Equal(ApplicationUpdateState.NotInstalled, service.Status.State);
        Assert.Equal("UpdateNotInstalled", service.Status.MessageKey);
        Assert.Equal("https://github.com/DamianMorozov/Open-LLM-Cluster/releases", service.ReleasesUrl);
        Assert.True(await service.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, adapter.Calls);
        Assert.Equal(ApplicationUpdateState.NotInstalled, service.Status.State);
    }

    [Fact]
    public async Task CheckReportsAvailableVersionAndDoesNotDownloadOrInstall()
    {
        var adapter = new FakeAdapter { Release = new("0.2.301", "Release notes") };
        var service = new ApplicationUpdateService(() => adapter);
        var states = new List<ApplicationUpdateState>();
        service.StatusChanged += (_, _) => states.Add(service.Status.State);
        Assert.Equal(ApplicationUpdateState.Idle, service.Status.State);
        Assert.Equal(0, adapter.Calls);
        Assert.True(await service.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { ApplicationUpdateState.Checking, ApplicationUpdateState.Available }, states);
        Assert.Equal("0.2.301", service.Status.Release!.Version);
        Assert.Equal("Release notes", service.Status.Release.NotesMarkdown);
        Assert.False(service.IsBusy);
    }

    [Fact]
    public async Task EmptyFeedClearsPreviouslyAvailableVersion()
    {
        var adapter = new FakeAdapter { Release = new("0.2.301", "") };
        var service = new ApplicationUpdateService(() => adapter);
        await service.CheckAsync(TestContext.Current.CancellationToken);
        adapter.Release = null;
        await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.NoUpdate, service.Status.State);
        Assert.Null(service.Status.Release);
    }

    [Fact]
    public async Task ConcurrentCheckIsRejectedUntilFirstActuallyCompletes()
    {
        var adapter = new FakeAdapter { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new ApplicationUpdateService(() => adapter);
        var first = service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(service.IsBusy);
        Assert.False(await service.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, adapter.Calls);
        adapter.Pending.SetResult(null);
        await first;
        Assert.False(service.IsBusy);
        Assert.Equal(ApplicationUpdateState.NoUpdate, service.Status.State);
    }

    [Fact]
    public async Task NetworkErrorIsReportedAndRetryIsAllowed()
    {
        var adapter = new FakeAdapter { Failure = new HttpRequestException("offline") };
        var service = new ApplicationUpdateService(() => adapter);
        await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.Error, service.Status.State);
        Assert.Equal("UpdateNetworkError", service.Status.MessageKey);
        Assert.False(service.IsBusy);
        adapter.Failure = null;
        await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.NoUpdate, service.Status.State);
        Assert.Equal(2, adapter.Calls);
    }

    [Fact]
    public async Task CancellationRestoresPreviousStateAndAllowsAnotherCheck()
    {
        var adapter = new FakeAdapter { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new ApplicationUpdateService(() => adapter);
        using var cancellation = new CancellationTokenSource();
        var task = service.CheckAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(ApplicationUpdateState.Idle, service.Status.State);
        Assert.False(service.IsBusy);
        adapter.Pending = null;
        await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.NoUpdate, service.Status.State);
    }

    [Fact]
    public async Task InstallationDetectionFailureDoesNotPreventAppStartupAndCanBeRetried()
    {
        var attempts = 0;
        var service = new ApplicationUpdateService(() => ++attempts == 1 ? throw new IOException("fixture") : new FakeAdapter());
        Assert.Equal(ApplicationUpdateState.Error, service.Status.State);
        await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.NoUpdate, service.Status.State);
        Assert.Equal(2, attempts);
    }

    private sealed class FakeAdapter : IApplicationUpdateAdapter
    {
        public bool IsInstalled { get; set; } = true;
        public string CurrentVersion => "0.2.300";
        public string Channel => "win-x64-stable";
        public int Calls { get; private set; }
        public AvailableApplicationUpdate? Release { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource<AvailableApplicationUpdate?>? Pending { get; set; }
        public Task DownloadAsync(Action<int> progress, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateReadyAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void ApplyAndRestart() => throw new NotSupportedException();
        public Task<AvailableApplicationUpdate?> CheckAsync(CancellationToken ct = default)
        {
            Calls++;
            return Failure is not null ? Task.FromException<AvailableApplicationUpdate?>(Failure)
                : Pending?.Task.WaitAsync(ct) ?? Task.FromResult(Release);
        }
    }
}
