// Проверяет загрузку и SHA-512 на подменённом HTTP; системный установщик в этих тестах не запускается
namespace OpenLLMCluster.Tests;

public sealed class RuntimeInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "open-llm-cluster-installer-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] _payload = Encoding.UTF8.GetBytes("test installer payload");
    public static bool IsNonWindows => !OperatingSystem.IsWindows();

    [Theory(SkipWhen = nameof(IsNonWindows), Skip = "Установка runtime через UI поддерживается в Windows")]
    [InlineData("x64")]
    [InlineData("arm64")]
    [InlineData("x86")]
    public async Task DownloadsMatchingArchitectureVerifiesHashAndReportsRestart(string architecture)
    {
        using var handler = new DownloadHandler(Manifest(architecture, Convert.ToHexString(SHA512.HashData(_payload))), _payload);
        using var client = new HttpClient(handler);
        var launched = new List<string>();
        var progress = new CollectProgress();
        var installer = new AspNetRuntimeInstaller(client, _directory, async (path, ct) =>
        {
            launched.Add(Path.GetFileName(path));
            Assert.EndsWith($"win-{architecture}.exe", path);
            Assert.Equal(_payload, await File.ReadAllBytesAsync(path, ct));
            Assert.Contains(progress.Values, item => item.Stage == "RuntimeVerifying");
            return launched.Count == 1 ? 3010 : 0;
        });
        var result = await installer.InstallAsync(architecture, progress, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { $"dotnet-runtime-10.0.15-win-{architecture}.exe", $"aspnetcore-runtime-10.0.15-win-{architecture}.exe" }, launched);
        Assert.Equal("10.0.15", result.Version);
        Assert.True(result.RestartRequired);
        Assert.Contains(handler.Urls, url => url.EndsWith($"win-{architecture}.exe"));
        Assert.Contains(progress.Values, item => item.Stage == "RuntimeDownloading" && item.Percent == 100);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact(SkipWhen = nameof(IsNonWindows), Skip = "Установка runtime через UI поддерживается в Windows")]
    public async Task HashMismatchPreventsLaunchAndDeletesDownloadedFile()
    {
        using var handler = new DownloadHandler(Manifest("x64", Convert.ToHexString(SHA512.HashData(_payload))), [1, 2, 3]);
        using var client = new HttpClient(handler);
        var launched = false;
        var installer = new AspNetRuntimeInstaller(client, _directory, (_, _) => { launched = true; return Task.FromResult(0); });
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync("x64", ct: TestContext.Current.CancellationToken));
        Assert.False(launched);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Theory(SkipWhen = nameof(IsNonWindows), Skip = "Установка runtime через UI поддерживается в Windows")]
    [InlineData(1602)]
    [InlineData(1223)]
    public async Task CancelledInstallerRemainsCancellationAndCleansDownload(int exitCode)
    {
        using var handler = new DownloadHandler(Manifest("x64", Convert.ToHexString(SHA512.HashData(_payload))), _payload);
        using var client = new HttpClient(handler);
        var installer = new AspNetRuntimeInstaller(client, _directory, (_, _) => Task.FromResult(exitCode));
        await Assert.ThrowsAsync<OperationCanceledException>(() => installer.InstallAsync("x64", ct: TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact(SkipWhen = nameof(IsNonWindows), Skip = "Установка runtime через UI поддерживается в Windows")]
    public async Task DownloadFailureDoesNotLaunchInstaller()
    {
        using var handler = new DownloadHandler(Manifest("x64", Convert.ToHexString(SHA512.HashData(_payload))), _payload, failDownload: true);
        using var client = new HttpClient(handler);
        var launched = false;
        var installer = new AspNetRuntimeInstaller(client, _directory, (_, _) => { launched = true; return Task.FromResult(0); });
        await Assert.ThrowsAsync<HttpRequestException>(() => installer.InstallAsync("x64", ct: TestContext.Current.CancellationToken));
        Assert.False(launched);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact(SkipWhen = nameof(IsNonWindows), Skip = "Установка runtime через UI поддерживается в Windows")]
    public async Task CorruptedAspNetPackagePreventsBothInstallersFromLaunching()
    {
        using var handler = new DownloadHandler(Manifest("x64", Convert.ToHexString(SHA512.HashData(_payload))), _payload, corruptAspNet: true);
        using var client = new HttpClient(handler);
        var calls = 0;
        var installer = new AspNetRuntimeInstaller(client, _directory, (_, _) => { calls++; return Task.FromResult(0); });
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync("x64", ct: TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
        Assert.Contains(handler.Urls, url => url.Contains("/Runtime/10.0.15/dotnet-runtime"));
        Assert.Contains(handler.Urls, url => url.Contains("/aspnetcore/Runtime/10.0.15/"));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact(SkipWhen = nameof(IsNonWindows), Skip = "Установка runtime через UI поддерживается в Windows")]
    public async Task AspNetInstallationFailureDoesNotReportSuccessAfterNetCoreInstalled()
    {
        using var handler = new DownloadHandler(Manifest("x64", Convert.ToHexString(SHA512.HashData(_payload))), _payload);
        using var client = new HttpClient(handler);
        var calls = 0;
        var installer = new AspNetRuntimeInstaller(client, _directory, (_, _) => Task.FromResult(++calls == 1 ? 0 : 1603));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync("x64", ct: TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
        Assert.Contains("aspnetcore-runtime", error.Message);
        Assert.Contains("1603", error.Message);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void OtherStartupFailuresAreNotMisclassifiedAsMissingAspNet()
    {
        Assert.IsType<InvalidOperationException>(AspNetRuntimeMissingException.FromAgentExit(1, "Address already in use"));
        Assert.IsType<InvalidOperationException>(AspNetRuntimeMissingException.FromAgentExit(1, "Microsoft.AspNetCore.App application exception"));
        var error = Assert.IsType<AspNetRuntimeMissingException>(AspNetRuntimeMissingException.FromAgentExit(unchecked((int)0x80008096),
            "Architecture: arm64\nFramework: 'Microsoft.AspNetCore.App', version '10.0.0'"));
        Assert.Equal("arm64", error.Architecture);
    }

    [Fact]
    public void OutdatedNetCoreRuntimeIsRecognizedForRecovery()
    {
        var error = Assert.IsType<AspNetRuntimeMissingException>(AspNetRuntimeMissingException.FromAgentExit(unchecked((int)0x80008096),
            "You must install or update .NET.\nArchitecture: x64\nFramework: 'Microsoft.NETCore.App', version '10.0.15'\nFound: 10.0.3"));
        Assert.Equal("x64", error.Architecture);
    }

    // Формирует метаданные Microsoft с устаревшей и предварительной версиями для проверки выбора стабильной
    private static string Manifest(string architecture, string hash) => $$$"""
        {"releases":[
          {"runtime":{"version":"10.0.2","files":[{"rid":"win-{{{architecture}}}","name":"dotnet-runtime-win-{{{architecture}}}.exe",
            "url":"https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.2/dotnet-runtime-win-{{{architecture}}}.exe","hash":"{{{hash}}}"}]},
            "aspnetcore-runtime":{"version":"10.0.2","files":[{"rid":"win-{{{architecture}}}","name":"aspnetcore-runtime-win-{{{architecture}}}.exe",
            "url":"https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/10.0.2/aspnetcore-runtime-win-{{{architecture}}}.exe","hash":"{{{hash}}}"}]}},
          {"runtime":{"version":"10.0.15","files":[{"rid":"win-{{{architecture}}}","name":"dotnet-runtime-win-{{{architecture}}}.exe",
            "url":"https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.15/dotnet-runtime-win-{{{architecture}}}.exe","hash":"{{{hash}}}"}]},
            "aspnetcore-runtime":{"version":"10.0.15","files":[{"rid":"win-{{{architecture}}}","name":"aspnetcore-runtime-win-{{{architecture}}}.exe",
            "url":"https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/10.0.15/aspnetcore-runtime-win-{{{architecture}}}.exe","hash":"{{{hash}}}"}]}},
          {"runtime":{"version":"10.0.99-preview.1","files":[]},"aspnetcore-runtime":{"version":"10.0.99-preview.1","files":[]}}
        ]}
        """;

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private sealed class CollectProgress : IProgress<RuntimeInstallProgress>
    {
        public List<RuntimeInstallProgress> Values { get; } = [];
        public void Report(RuntimeInstallProgress value) => Values.Add(value);
    }

    private sealed class DownloadHandler(string manifest, byte[] payload, bool failDownload = false, bool corruptAspNet = false) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            Urls.Add(url);
            var metadata = url == AspNetRuntimeInstaller.MetadataUrl;
            return Task.FromResult(new HttpResponseMessage(!metadata && failDownload ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = metadata ? new StringContent(manifest)
                    : new ByteArrayContent(corruptAspNet && url.Contains("/aspnetcore/") ? [1, 2, 3] : payload)
            });
        }
    }
}
