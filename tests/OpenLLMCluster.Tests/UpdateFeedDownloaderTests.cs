// Проверяет отмену реального HTTP-запроса метаданных без обращения к внешнему сервису
namespace OpenLLMCluster.Tests;

public sealed class UpdateFeedDownloaderTests
{
    [Fact]
    public async Task CancellationStopsRealPackageTransferAfterProgress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var target = Path.Combine(Path.GetTempPath(), "olc-http-" + Guid.NewGuid().ToString("N") + ".partial");
        using var cancellation = new CancellationTokenSource();
        var progressSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloader = new UpdateFeedDownloader(cancellation.Token);
        var request = downloader.DownloadFile($"http://127.0.0.1:{port}/package", target,
            value => { if (value > 0) progressSeen.TrySetResult(); }, headers: null, timeout: 30, cancellation.Token);
        try
        {
            using var connection = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = connection.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\n\r\n"),
                TestContext.Current.CancellationToken);
            await stream.WriteAsync(new byte[10000], TestContext.Current.CancellationToken);
            await progressSeen.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(request.IsCompleted);
        }
        finally { cancellation.Cancel(); if (File.Exists(target)) File.Delete(target); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStopsRealHttpRequest(bool bytes)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource();
        var downloader = new UpdateFeedDownloader(cancellation.Token);
        var url = $"http://127.0.0.1:{port}/metadata";
        Task request = bytes ? downloader.DownloadBytes(url, headers: null, timeout: 30) : downloader.DownloadString(url, headers: null, timeout: 30);
        using var connection = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(request.IsCompleted);
    }
}
