// Использует настоящий GithubSource и UpdateManager на подменённых ответах GitHub без установки и скачивания пакетов
namespace OpenLLMCluster.Tests;

public sealed class VelopackUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "olc-update-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    public async Task StableInstallationReadsOnlyItsRidAndExcludesGithubPrereleases(string rid)
    {
        var downloader = new MetadataDownloader(rid);
        var adapter = CreateAdapter(rid, rid + "-stable", downloader);
        Assert.Empty(downloader.Requests);
        var result = await adapter.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal("0.2.301", result!.Version);
        Assert.Equal("Test notes", result.NotesMarkdown);
        Assert.Equal(2, downloader.Requests.Count);
        Assert.Contains($"https://example.invalid/releases.{rid}-stable.json", downloader.Requests);
        Assert.DoesNotContain(downloader.Requests, url => url.Contains("beta"));
        Assert.False(downloader.AuthorizationSeen);
        Assert.Equal(0, downloader.PackageDownloads);
    }

    [Fact]
    public async Task BetaInstallationReadsItsOwnPrereleaseChannel()
    {
        var downloader = new MetadataDownloader("osx-arm64");
        var adapter = CreateAdapter("osx-arm64", "osx-arm64-beta", downloader);
        var result = await adapter.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal("0.2.400-beta.1", result!.Version);
        Assert.Contains("https://example.invalid/releases.osx-arm64-beta.json", downloader.Requests);
        Assert.DoesNotContain(downloader.Requests, url => url.Contains("-stable"));
    }

    [Theory]
    [InlineData("win-x64", "linux-x64-stable")]
    [InlineData("win-x64", "stable")]
    [InlineData("osx-arm64", "osx-x64-beta")]
    [InlineData("linux-x64", "")]
    public void WrongOrMissingInstalledChannelIsRejectedBeforeNetwork(string rid, string channel)
    {
        var downloader = new MetadataDownloader(rid);
        Assert.Throws<InvalidDataException>(() => CreateAdapter(rid, channel, downloader));
        Assert.Empty(downloader.Requests);
    }

    [Theory]
    [InlineData("0.2.300")]
    [InlineData("0.2.299")]
    public async Task SameAndOlderVersionsDoNotBecomeUpdates(string remoteVersion)
    {
        var downloader = new MetadataDownloader("win-x64") { StableVersion = remoteVersion };
        Assert.Null(await CreateAdapter("win-x64", "win-x64-stable", downloader).CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, downloader.PackageDownloads);
    }

    [Theory]
    [InlineData("OtherApp", "0.2.301")]
    [InlineData("OpenLLMCluster", "0.2.400-beta.1")]
    public async Task ForeignAppAndPrereleaseInStableFeedAreRejected(string packageId, string version)
    {
        var downloader = new MetadataDownloader("win-x64") { PackageId = packageId, StableVersion = version };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateAdapter("win-x64", "win-x64-stable", downloader).CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, downloader.PackageDownloads);
    }

    [Fact]
    public async Task NoPublicReleasesIsAValidNoUpdateResult()
    {
        var downloader = new MetadataDownloader("win-x64") { NoReleases = true };
        Assert.Null(await CreateAdapter("win-x64", "win-x64-stable", downloader).CheckAsync(TestContext.Current.CancellationToken));
        Assert.Single(downloader.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpFailureAndInvalidJsonBecomeServiceErrors(bool invalidJson)
    {
        var downloader = new MetadataDownloader("win-x64") { InvalidJson = invalidJson, Offline = !invalidJson };
        var service = new ApplicationUpdateService(() => CreateAdapter("win-x64", "win-x64-stable", downloader));
        await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.Error, service.Status.State);
        Assert.Equal(invalidJson ? "UpdateError" : "UpdateNetworkError", service.Status.MessageKey);
        Assert.Null(service.Status.Release);
        Assert.Equal(0, downloader.PackageDownloads);
    }

    [Fact]
    public async Task RealUnpackagedDetectionSkipsDownloaderEntirely()
    {
        var locator = VelopackLocator.CreateDefaultForPlatform(logger: new Velopack.Logging.NullVelopackLogger());
        var adapter = new VelopackUpdateAdapter(locator, createDownloader: _ => throw new InvalidOperationException("Сеть не должна вызываться"));
        Assert.False(adapter.IsInstalled);
        var service = new ApplicationUpdateService(() => adapter);
        await service.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationUpdateState.NotInstalled, service.Status.State);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void UpdateStatusMessagesAreLocalized(string language)
    {
        var localizer = new Localizer();
        localizer.SetLanguage(language);
        foreach (var key in new[] { "UpdateNotInstalled", "UpdateIdle", "UpdateChecking", "UpdateNoUpdate", "UpdateAvailable", "UpdateError", "UpdateNetworkError" })
            Assert.NotEqual(key, localizer[key]);
    }

    // Создаёт изолированную установку только для определения версии и канала
    private VelopackUpdateAdapter CreateAdapter(string rid, string channel, IFileDownloader downloader)
    {
        Directory.CreateDirectory(_directory);
        var locator = new TestVelopackLocator(ApplicationInfo.PackageId, "0.2.300", _directory, null, null, null, channel);
        return new(locator, rid, _ => downloader);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private sealed class MetadataDownloader(string rid) : HttpClientFileDownloader
    {
        public List<string> Requests { get; } = [];
        public bool AuthorizationSeen { get; private set; }
        public int PackageDownloads { get; private set; }
        public string StableVersion { get; set; } = "0.2.301";
        public string PackageId { get; set; } = "OpenLLMCluster";
        public bool NoReleases { get; set; }
        public bool Offline { get; set; }
        public bool InvalidJson { get; set; }

        public override Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
        {
            Capture(url, headers);
            Assert.StartsWith("https://api.github.com/repos/DamianMorozov/Open-LLM-Cluster/releases?", url);
            if (Offline) throw new HttpRequestException("fixture offline");
            if (InvalidJson) return Task.FromResult("invalid json");
            if (NoReleases) return Task.FromResult("[]");
            return Task.FromResult(JsonSerializer.Serialize(new[] { Release(prerelease: false), Release(prerelease: true) }));
        }

        public override Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
        {
            Capture(url, headers);
            var version = url.Contains("-beta") ? "0.2.400-beta.1" : StableVersion;
            return Task.FromResult(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Assets = new[] { new {
                PackageId, Version = version, Type = "Full", FileName = $"{PackageId}-{version}-{rid}-full.nupkg", Size = 123,
                SHA256 = new string('A', 64), NotesMarkdown = "Test notes"
            } } })));
        }

        public override Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers, double timeout,
            CancellationToken cancelToken = default)
        {
            PackageDownloads++;
            throw new InvalidOperationException("Проверка версии не должна скачивать пакет");
        }

        // Возвращает настоящий формат метаданных GitHub с двумя разными каналами
        private object Release(bool prerelease)
        {
            var channel = rid + (prerelease ? "-beta" : "-stable");
            var name = $"releases.{channel}.json";
            return new { name = channel, prerelease, published_at = prerelease ? "2026-11-02T00:00:00Z" : "2026-11-01T00:00:00Z",
                assets = new[] { new { name, browser_download_url = "https://example.invalid/" + name } } };
        }

        // Фиксирует URL и проверяет отсутствие токена в запросах публичного источника
        private void Capture(string url, IDictionary<string, string>? headers)
        {
            Requests.Add(url);
            AuthorizationSeen |= headers?.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) == true;
        }
    }
}
