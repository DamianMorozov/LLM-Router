// Проверяет закреплённый релиз, SHA-256, CUDA-зависимости, публикацию каталога и отказ без RPC на подменённом HTTP
namespace OpenLLMCluster.Tests;

public sealed class LlamaInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "open-llm-cluster-packages-" + Guid.NewGuid().ToString("N"));
    public static bool IsMac => OperatingSystem.IsMacOS();

    [Fact]
    public async Task VerifiedBuildIsActivatedAndSameVersionIsReusedWithoutDownloading()
    {
        var store = new LlamaInstallationStore(_root);
        using var handler = new PackageHandler(LlamaBackend.Cpu);
        using var client = new HttpClient(handler);
        var checks = 0;
        var installer = new LlamaPackageInstaller(client, store, (directory, _) =>
        {
            Assert.True(File.Exists(Path.Combine(directory, RuntimePlanner.GetExecutableName(NodeRole.Worker))));
            checks++;
            return Task.FromResult("fixture (build 12318)");
        });
        var installed = await installer.InstallAsync("b12318", LlamaBackend.Cpu, ct: TestContext.Current.CancellationToken);
        Assert.Equal(installed, store.ReadCurrent());
        Assert.Equal(installed, LlamaInstallationStore.ReadDirectory(installed.Directory));
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Contains("/latest"));
        var reused = await installer.InstallAsync("b12318", LlamaBackend.Cpu, ct: TestContext.Current.CancellationToken);
        Assert.Equal(installed, reused);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, checks);
        Assert.Empty(Directory.EnumerateDirectories(_root, ".install-*"));
    }

    [Fact(SkipWhen = nameof(IsMac), Skip = "CUDA-пакеты доступны для Windows/Linux")]
    public async Task CudaLibrariesAreVerifiedAndExtractedBeforeExecutableValidation()
    {
        using var handler = new PackageHandler(LlamaBackend.Cuda);
        using var client = new HttpClient(handler);
        var installer = new LlamaPackageInstaller(client, new(_root), (directory, _) =>
        {
            Assert.Equal("CUDA dependency", File.ReadAllText(Path.Combine(directory, "cuda-dependency.txt")));
            return Task.FromResult("fixture (build 12318)");
        });
        var installed = await installer.InstallAsync("b12318", LlamaBackend.Cuda, ct: TestContext.Current.CancellationToken);
        Assert.Equal(LlamaBackend.Cuda, installed.Backend);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests.Skip(1), request => Assert.Contains("/download/b12318/", request));
    }

    [Fact]
    public async Task ReusedInstallationRejectsChangedExecutableVersion()
    {
        using var handler = new PackageHandler(LlamaBackend.Cpu);
        using var client = new HttpClient(handler);
        var version = "fixture (build 12318)";
        var store = new LlamaInstallationStore(_root);
        var installer = new LlamaPackageInstaller(client, store, (_, _) => Task.FromResult(version));
        await installer.InstallAsync("b12318", LlamaBackend.Cpu, ct: TestContext.Current.CancellationToken);
        version = "fixture (build 12319)";
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync("b12318", LlamaBackend.Cpu,
            ct: TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task BuildNumberPrefixDoesNotMatchSelectedRelease()
    {
        using var handler = new PackageHandler(LlamaBackend.Cpu);
        using var client = new HttpClient(handler);
        var store = new LlamaInstallationStore(_root);
        var installer = new LlamaPackageInstaller(client, store, (_, _) => Task.FromResult("fixture (build 123180)"));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync("b12318", LlamaBackend.Cpu,
            ct: TestContext.Current.CancellationToken));
        Assert.Null(store.ReadCurrent());
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("rpc")]
    [InlineData("traversal")]
    public async Task InvalidPackageNeverBecomesCurrentBuild(string failure)
    {
        using var handler = new PackageHandler(LlamaBackend.Cpu, failure);
        using var client = new HttpClient(handler);
        var validated = false;
        var store = new LlamaInstallationStore(_root);
        var installer = new LlamaPackageInstaller(client, store, (_, _) => { validated = true; return Task.FromResult("build 12318"); });
        await Assert.ThrowsAnyAsync<Exception>(() => installer.InstallAsync("b12318", LlamaBackend.Cpu, ct: TestContext.Current.CancellationToken));
        Assert.False(validated);
        Assert.Null(store.ReadCurrent());
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    [Theory]
    [InlineData("../main")]
    [InlineData("latest")]
    [InlineData("b12318/other")]
    public void InvalidReleaseCannotBecomePathOrUrl(string tag) => Assert.Throws<ArgumentException>(() => LlamaPackageInstaller.ValidateTag(tag));

    [Theory]
    [InlineData("win-x64", "12.4", ".zip")]
    [InlineData("ubuntu-x64", "12.8", ".tar.gz")]
    [InlineData("win-arm64", "13.5", ".zip")]
    [InlineData("ubuntu-arm64", "13.5", ".tar.gz")]
    public void CudaAssetPlanUsesOneReleaseAndIncludesRuntime(string platform, string cuda, string extension)
    {
        var names = LlamaPackageInstaller.GetAssetNames("b12318", LlamaBackend.Cuda, platform);
        Assert.Equal(2, names.Length);
        Assert.StartsWith("llama-b12318-", names[0]);
        Assert.StartsWith("cudart-llama-", names[1]);
        Assert.All(names, name => { Assert.Contains("cuda-" + cuda, name); Assert.EndsWith(extension, name); });
    }

    [Fact]
    public void ClusterRejectsMixedVersionsOrMissingRpc()
    {
        var local = new LlamaInstallation("b12318", LlamaBackend.Cuda, "win-x64", "fixture", "build 12318");
        var remote = new NodeSnapshot { LlamaReleaseTag = "b12318", LlamaRpcAvailable = true, LlamaBackend = "Cpu" };
        ClusterBuildGuard.RequireCompatible("b12318", local, remote, "worker");
        remote.LlamaReleaseTag = "b12317";
        Assert.Throws<InvalidOperationException>(() => ClusterBuildGuard.RequireCompatible("b12318", local, remote, "worker"));
        remote.LlamaReleaseTag = "b12318";
        remote.LlamaRpcAvailable = false;
        Assert.Throws<InvalidOperationException>(() => ClusterBuildGuard.RequireCompatible("b12318", local, remote, "worker"));
        Assert.Throws<InvalidOperationException>(() => ClusterBuildGuard.RequireCompatible("b12318", null, remote, "worker"));
    }

    /// <summary>Удаляет временные загрузки теста</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private sealed class PackageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _packages = new();
        private readonly string _manifest;
        public List<string> Requests { get; } = [];

        public PackageHandler(LlamaBackend backend, string failure = "")
        {
            var names = LlamaPackageInstaller.GetAssetNames("b12318", backend, LlamaPackageInstaller.GetPlatform());
            foreach (var name in names)
            {
                var entries = name.StartsWith("cudart", StringComparison.Ordinal) ? new Dictionary<string, string> { ["cuda-dependency.txt"] = "CUDA dependency" }
                    : new Dictionary<string, string> { [RuntimePlanner.GetExecutableName(NodeRole.Coordinator)] = "server" };
                if (!name.StartsWith("cudart", StringComparison.Ordinal) && failure != "rpc") entries[RuntimePlanner.GetExecutableName(NodeRole.Worker)] = "rpc";
                if (failure == "traversal") entries["../outside.txt"] = "invalid";
                using var stream = new MemoryStream();
                if (name.EndsWith(".zip", StringComparison.Ordinal))
                {
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                        foreach (var entry in entries)
                        { using var writer = new StreamWriter(zip.CreateEntry(entry.Key).Open()); writer.Write(entry.Value); }
                }
                else
                {
                    using var gzip = new GZipStream(stream, CompressionMode.Compress, leaveOpen: true);
                    using var tar = new System.Formats.Tar.TarWriter(gzip, leaveOpen: true);
                    foreach (var entry in entries)
                    {
                        using var data = new MemoryStream(Encoding.UTF8.GetBytes(entry.Value));
                        tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, entry.Key) { DataStream = data });
                    }
                }
                _packages[name] = stream.ToArray();
            }
            _manifest = JsonSerializer.Serialize(new { tag_name = "b12318", assets = _packages.Select(item => new { name = item.Key,
                browser_download_url = "https://github.com/ggml-org/llama.cpp/releases/download/b12318/" + item.Key,
                digest = "sha256:" + (failure == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(item.Value))) }) });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            var metadata = url.Contains("api.github.com", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = metadata ? new StringContent(_manifest)
                : new ByteArrayContent(_packages[Path.GetFileName(request.RequestUri.AbsolutePath)]) });
        }
    }
}
