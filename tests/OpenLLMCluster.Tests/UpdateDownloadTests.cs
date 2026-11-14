// Проверяет прогресс, проверку перед готовностью, отмену, повтор скачивания и запрет параллельных операций
namespace OpenLLMCluster.Tests;

public sealed class UpdateDownloadTests
{
    [Fact]
    public async Task ProgressReaching100DoesNotMakeUnverifiedPackageReady()
    {
        var adapter = new UpdateTestAdapter { DownloadWait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new ApplicationUpdateService(() => adapter);
        Assert.False(await service.DownloadAsync(TestContext.Current.CancellationToken));
        await service.CheckAsync(TestContext.Current.CancellationToken);
        var download = service.DownloadAsync(TestContext.Current.CancellationToken);
        adapter.Progress!(100);
        Assert.Equal(100, service.Status.Progress);
        Assert.Equal(ApplicationUpdateState.Downloading, service.Status.State);
        Assert.False(await service.CheckAsync(TestContext.Current.CancellationToken));
        Assert.False(await service.DownloadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, adapter.Checks);
        Assert.Equal(1, adapter.Downloads);
        adapter.DownloadWait.SetResult();
        await download;
        Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
        Assert.Equal("0.2.301", service.Status.Release!.Version);
    }

    [Fact]
    public async Task VerificationFailureRetainsCandidateAndRetryCanSucceed()
    {
        var adapter = new UpdateTestAdapter { DownloadFailure = new InvalidDataException("fixture checksum") };
        var service = new ApplicationUpdateService(() => adapter);
        await service.CheckAsync(TestContext.Current.CancellationToken);
        await service.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.Error, service.Status.State);
        Assert.Equal("UpdateError", service.Status.MessageKey);
        Assert.NotNull(service.Status.Release);
        Assert.False(service.IsBusy);
        adapter.DownloadFailure = null;
        await service.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
        Assert.Equal(2, adapter.Downloads);
        Assert.Equal(1, adapter.Checks);
    }

    [Fact]
    public async Task CancelStopsActualAdapterOperationAndAllowsRetry()
    {
        var adapter = new UpdateTestAdapter { DownloadWait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new ApplicationUpdateService(() => adapter);
        await service.CheckAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var download = service.DownloadAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.True(adapter.DownloadCancelled);
        Assert.Equal(ApplicationUpdateState.Available, service.Status.State);
        Assert.Equal("UpdateDownloadCancelled", service.Status.MessageKey);
        Assert.False(service.IsBusy);
        adapter.DownloadWait = null;
        await service.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
    }
}

// Управляемая операция позволяет проверять интерфейс во время реальной незавершённой задачи
internal sealed class UpdateTestAdapter : IApplicationUpdateAdapter
{
    public bool IsInstalled { get; set; } = true;
    public string CurrentVersion => "0.2.300";
    public string Channel => "win-x64-stable";
    public AvailableApplicationUpdate? Release { get; set; } = new("0.2.301", "# Notes\n<script>alert('test')</script>\n[link](javascript:alert(1))");
    public int Checks { get; private set; }
    public int Downloads { get; private set; }
    public Exception? DownloadFailure { get; set; }
    public TaskCompletionSource? DownloadWait { get; set; }
    public TaskCompletionSource<AvailableApplicationUpdate?>? CheckWait { get; set; }
    public Action<int>? Progress { get; private set; }
    public bool DownloadCancelled { get; private set; }
    public bool CheckCancelled { get; private set; }
    public int Applies { get; private set; }
    public Exception? ValidationFailure { get; set; }
    public Exception? ApplyFailure { get; set; }
    public Action? OnApply { get; set; }
    public TaskCompletionSource? ValidationWait { get; set; }

    public async Task ValidateReadyAsync(CancellationToken ct = default)
    {
        if (ValidationWait is not null) await ValidationWait.Task.WaitAsync(ct);
        if (ValidationFailure is not null) throw ValidationFailure;
    }
    public void ApplyAndRestart()
    {
        if (ApplyFailure is not null) throw ApplyFailure;
        OnApply?.Invoke();
        Applies++;
    }

    public async Task<AvailableApplicationUpdate?> CheckAsync(CancellationToken ct = default)
    {
        Checks++;
        try { return CheckWait is null ? Release : await CheckWait.Task.WaitAsync(ct); }
        catch (OperationCanceledException) { CheckCancelled = true; throw; }
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken ct = default)
    {
        Downloads++;
        Progress = progress;
        progress(40);
        try
        {
            if (DownloadWait is not null) await DownloadWait.Task.WaitAsync(ct);
            if (DownloadFailure is not null) throw DownloadFailure;
            progress(100);
        }
        catch (OperationCanceledException) { DownloadCancelled = true; throw; }
    }
}
