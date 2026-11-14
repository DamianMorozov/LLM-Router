// Проверяет скачивание настоящим UpdateManager, целостность пакета, повтор и отмену без установки
namespace OpenLLMCluster.Tests;

public sealed class VelopackDownloadTests : IDisposable
{
    private const string PackageName = "OpenLLMCluster-0.2.301-win-x64-full.nupkg";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "olc-package-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _package = CreatePackage();
    private bool _corruptDownload;
    private TaskCompletionSource? _pending;
    private int _downloads;
    private bool _cancelled;
    private TestVelopackLocator? _locator;

    [Fact]
    public async Task ActualManagerDownloadsSelectedAssetAndVerifiesPackageBeforeReady()
    {
        var service = CreateService();
        using var check = new CancellationTokenSource();
        await service.CheckAsync(check.Token);
        check.Cancel();
        await service.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
        Assert.Equal(_package, await File.ReadAllBytesAsync(Path.Combine(_directory, PackageName), TestContext.Current.CancellationToken));
        Assert.Equal(1, _downloads);
        Assert.Null(_locator!.ExitCode);
    }

    [Fact]
    public async Task CorruptDownloadedPackageNeverBecomesReadyAndRetryReplacesPartialFile()
    {
        var service = CreateService();
        await service.CheckAsync(TestContext.Current.CancellationToken);
        _corruptDownload = true;
        await service.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.Error, service.Status.State);
        Assert.False(File.Exists(Path.Combine(_directory, PackageName)));
        _corruptDownload = false;
        await service.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
        Assert.Equal(2, _downloads);
    }

    [Fact]
    public async Task CorruptExistingPackageIsReDownloadedInsteadOfAcceptedByVelopackShortcut()
    {
        var service = CreateService();
        await service.CheckAsync(TestContext.Current.CancellationToken);
        var corrupt = (byte[])_package.Clone();
        corrupt[^1] ^= 1;
        await File.WriteAllBytesAsync(Path.Combine(_directory, PackageName), corrupt, TestContext.Current.CancellationToken);
        await service.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
        Assert.Equal(1, _downloads);
        Assert.Equal(_package, await File.ReadAllBytesAsync(Path.Combine(_directory, PackageName), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidExistingPackageIsVerifiedWithoutAnotherDownload()
    {
        var service = CreateService();
        await service.CheckAsync(TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_directory, PackageName), _package, TestContext.Current.CancellationToken);
        await service.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task PackageChangedAfterDownloadCannotStopWorkOrRunUpdater()
    {
        var service = CreateService();
        await service.CheckAsync(TestContext.Current.CancellationToken);
        await service.DownloadAsync(TestContext.Current.CancellationToken);
        var corrupt = (byte[])_package.Clone();
        corrupt[^1] ^= 1;
        await File.WriteAllBytesAsync(Path.Combine(_directory, PackageName), corrupt, TestContext.Current.CancellationToken);
        var prepared = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ApplyAsync(_ =>
        {
            prepared = true;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken));
        Assert.False(prepared);
        Assert.Null(_locator!.ExitCode);
        Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
    }

    [Fact]
    public async Task MissingUpdaterCannotStopWorkAndDoesNotDiscardVerifiedPackage()
    {
        var service = CreateService();
        await service.CheckAsync(TestContext.Current.CancellationToken);
        await service.DownloadAsync(TestContext.Current.CancellationToken);
        var prepared = false;
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.ApplyAsync(_ =>
        {
            prepared = true;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken));
        Assert.False(prepared);
        Assert.Null(_locator!.ExitCode);
        Assert.Equal(_package, await File.ReadAllBytesAsync(Path.Combine(_directory, PackageName), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ActualManagerPassesCancellationIntoFileDownloadAndRetrySucceeds()
    {
        var service = CreateService();
        await service.CheckAsync(TestContext.Current.CancellationToken);
        _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var task = service.DownloadAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(_cancelled);
        Assert.Equal(ApplicationUpdateState.Available, service.Status.State);
        _pending = null;
        await service.DownloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
    }

    // Изолирует каталог пакетов и процесс установки от пользовательского приложения
    private ApplicationUpdateService CreateService()
    {
        Directory.CreateDirectory(_directory);
        _locator = new TestVelopackLocator(ApplicationInfo.PackageId, "0.2.300", _directory, _directory, _directory,
            Path.Combine(_directory, "Update.exe"), "win-x64-stable");
        return new ApplicationUpdateService(() => new VelopackUpdateAdapter(_locator, "win-x64", ct => new PackageDownloader(this, ct)));
    }

    // Формирует небольшой zip-пакет, чтобы тест проверял записанные байты, а не только фиктивные статусы
    private static byte[] CreatePackage()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("OpenLLMCluster.nuspec").Open());
            writer.Write("<package><metadata><id>OpenLLMCluster</id><version>0.2.301</version><authors>OLC</authors>"
                + "<description>Fixture</description></metadata></package>");
        }
        return memory.ToArray();
    }

    /// <summary>Удаляет только изолированный тестовый каталог</summary>
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private sealed class PackageDownloader(VelopackDownloadTests fixture, CancellationToken ct) : HttpClientFileDownloader
    {
        public override Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(JsonSerializer.Serialize(new[] { new {
                name = "0.2.301", prerelease = false, published_at = "2026-11-01T00:00:00Z",
                assets = new[] { "releases.win-x64-stable.json", PackageName }
                    .Select(name => new { name, browser_download_url = "https://example.invalid/" + name }).ToArray()
            } }));
        }

        public override Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Assets = new[] { new {
                PackageId = ApplicationInfo.PackageId, Version = "0.2.301", Type = "Full", FileName = PackageName,
                Size = fixture._package.Length, SHA256 = Convert.ToHexString(SHA256.HashData(fixture._package)), NotesMarkdown = "New version"
            } } })));
        }

        public override async Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers,
            double timeout, CancellationToken cancelToken = default)
        {
            Assert.Equal("https://example.invalid/" + PackageName, url);
            Assert.Equal(ct, cancelToken);
            fixture._downloads++;
            progress(40);
            try
            {
                if (fixture._pending is not null) await fixture._pending.Task.WaitAsync(cancelToken);
                var bytes = (byte[])fixture._package.Clone();
                if (fixture._corruptDownload) bytes[^1] ^= 1;
                await File.WriteAllBytesAsync(targetFile, bytes, cancelToken);
                progress(100);
            }
            catch (OperationCanceledException) { fixture._cancelled = true; throw; }
        }
    }
}
