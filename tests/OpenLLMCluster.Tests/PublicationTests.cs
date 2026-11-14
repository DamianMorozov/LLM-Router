// Проверяет реальные параметры MSBuild и блокировку несогласованной публикации без создания пакетов релиза
namespace OpenLLMCluster.Tests;

public sealed class PublicationTests
{
    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    public async Task PublishUsesVersionedReleaseFolderAndExternalRuntimes(string rid)
    {
        var result = await RunMsBuildAsync($"-p:RuntimeIdentifier={rid}", "-p:_IsPublishing=true",
            "-getProperty:PublishDir,GitHubReleaseDir,Version,SelfContained,PublishTrimmed,PublishAot,UseAppHost,VelopackChannel,VelopackPackId,MacOSBundleId");
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var properties = json.RootElement.GetProperty("Properties");
        var version = properties.GetProperty("Version").GetString()!;
        var expected = Path.GetFullPath(Path.Combine(RepositoryRoot(), "..", "Releases", version, rid));
        Assert.Equal(expected, Path.TrimEndingDirectorySeparator(Path.GetFullPath(properties.GetProperty("GitHubReleaseDir").GetString()!)));
        Assert.Equal(Path.Combine(expected, "publish"), Path.TrimEndingDirectorySeparator(Path.GetFullPath(properties.GetProperty("PublishDir").GetString()!)));
        Assert.Equal("false", properties.GetProperty("SelfContained").GetString());
        Assert.Equal("true", properties.GetProperty("UseAppHost").GetString());
        Assert.Equal("false", properties.GetProperty("PublishTrimmed").GetString());
        Assert.Equal("false", properties.GetProperty("PublishAot").GetString());
        Assert.Equal(rid + "-stable", properties.GetProperty("VelopackChannel").GetString());
        Assert.Equal("OpenLLMCluster", properties.GetProperty("VelopackPackId").GetString());
        Assert.Equal("io.github.damianmorozov.openllmcluster", properties.GetProperty("MacOSBundleId").GetString());
    }

    [Fact]
    public async Task RelativeReleaseRootIsResolvedFromRepositoryEvenWhenInvokedElsewhere()
    {
        var root = RepositoryRoot();
        var relativeRoot = Path.Combine("..", "Releases", "relative-check");
        var start = new ProcessStartInfo("pwsh")
            { WorkingDirectory = Path.GetTempPath(), RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(root, "tools", "Publish-Desktop.ps1"),
            "-RuntimeIdentifier", "win-x64", "-ReleaseRoot", relativeRoot, "-ValidateOnly" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var output = await stdout;
        Assert.True(process.ExitCode == 0, output + await stderr);
        using var json = JsonDocument.Parse(output);
        var version = json.RootElement.GetProperty("Version").GetString()!;
        var expected = Path.Combine(Path.GetFullPath(relativeRoot, root), version, "win-x64");
        Assert.Equal(expected, Path.TrimEndingDirectorySeparator(Path.GetFullPath(json.RootElement.GetProperty("GitHubReleaseDir").GetString()!)));
    }

    [Fact]
    public async Task OrdinaryBuildRemainsFrameworkDependent()
    {
        var result = await RunMsBuildAsync("-getProperty:SelfContained,RuntimeIdentifier");
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("false", json.RootElement.GetProperty("Properties").GetProperty("SelfContained").GetString());
        Assert.Equal("", json.RootElement.GetProperty("Properties").GetProperty("RuntimeIdentifier").GetString());
    }

    [Fact]
    public async Task PublishWithoutExplicitRidUsesSdkHost()
    {
        var result = await RunMsBuildAsync("-p:_IsPublishing=true", "-target:ValidatePublication",
            "-getProperty:RuntimeIdentifier,NETCoreSdkRuntimeIdentifier,PublishDir");
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var properties = json.RootElement.GetProperty("Properties");
        var rid = properties.GetProperty("RuntimeIdentifier").GetString()!;
        Assert.Equal(properties.GetProperty("NETCoreSdkRuntimeIdentifier").GetString(), rid);
        Assert.EndsWith(Path.Combine(rid, "publish"), Path.TrimEndingDirectorySeparator(Path.GetFullPath(properties.GetProperty("PublishDir").GetString()!)));
    }

    [Fact]
    public void LibraryAndLocalToolUseSameStableVersion()
    {
        var packages = System.Xml.Linq.XDocument.Load(Path.Combine(RepositoryRoot(), "Directory.Packages.props"));
        var version = packages.Descendants("PackageVersion").Single(item => (string?)item.Attribute("Include") == "Velopack").Attribute("Version")!.Value;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), ".config", "dotnet-tools.json")));
        Assert.DoesNotContain("-", version);
        Assert.Equal(version, manifest.RootElement.GetProperty("tools").GetProperty("vpk").GetProperty("version").GetString());
        Assert.True(manifest.RootElement.GetProperty("isRoot").GetBoolean());
        Assert.Contains(manifest.RootElement.GetProperty("tools").GetProperty("vpk").GetProperty("commands").EnumerateArray(),
            command => command.GetString() == "vpk");
    }

    [Theory]
    [InlineData("ReleaseTag=v9.9.9", "OLCPUB002")]
    [InlineData("PackageVersion=9.9.9", "OLCPUB003")]
    [InlineData("Version=9.9.9", "OLCPUB001")]
    [InlineData("ReleaseChannel=preview", "OLCPUB004")]
    [InlineData("SelfContained=true", "OLCPUB005")]
    [InlineData("PublishTrimmed=true", "OLCPUB005")]
    [InlineData("PublishAot=true", "OLCPUB005")]
    [InlineData("BundleAgent=false", "OLCPUB005")]
    [InlineData("RuntimeIdentifier=win-arm64", "OLCPUB006")]
    public async Task PublicationRejectsInconsistentInputsBeforeBuild(string property, string errorCode)
    {
        var result = await RunMsBuildAsync("-target:ValidatePublication", "-p:RuntimeIdentifier=win-x64", "-p:_IsPublishing=true", "-p:" + property);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(errorCode, result.Output);
    }

    [Fact]
    public async Task ValidPublicationAndExplicitCiRootAreAccepted()
    {
        var root = Path.Combine(Path.GetTempPath(), "olc-ci-publication");
        var result = await RunMsBuildAsync("-target:ValidatePublication", "-p:RuntimeIdentifier=linux-x64", "-p:_IsPublishing=true",
            "-p:GitHubReleaseRoot=" + root, "-p:ReleaseChannel=beta", "-getProperty:PublishDir,GitHubReleaseDir,Version,VelopackChannel");
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var properties = json.RootElement.GetProperty("Properties");
        var releaseDirectory = Path.Combine(root, properties.GetProperty("Version").GetString()!, "linux-x64");
        Assert.Equal(releaseDirectory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(properties.GetProperty("GitHubReleaseDir").GetString()!)));
        Assert.Equal(Path.Combine(releaseDirectory, "publish"),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(properties.GetProperty("PublishDir").GetString()!)));
        Assert.Equal("linux-x64-beta", properties.GetProperty("VelopackChannel").GetString());
    }

    [Theory]
    [InlineData("win-x64", "net10.0-x64-runtime,net10.0-x64-aspnetcore")]
    [InlineData("linux-x64", "")]
    [InlineData("osx-arm64", "")]
    [InlineData("osx-x64", "")]
    public async Task WindowsInstallerDeclaresBothExternalRuntimesForItsArchitecture(string rid, string expected)
    {
        var result = await RunMsBuildAsync($"-p:RuntimeIdentifier={rid}", "-p:_IsPublishing=true", "-getProperty:WindowsRuntimeDependencies");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.Output.Trim());
    }

    // Выполняет только вычисление свойств или раннюю проверку, без restore и компиляции
    private static async Task<(int ExitCode, string Output)> RunMsBuildAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = RepositoryRoot(), RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add("src/OpenLLMCluster.Desktop/OpenLLMCluster.Desktop.csproj");
        start.ArgumentList.Add("-nologo");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }

    // Находит корень исходников независимо от каталога запуска тестов
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Open-LLM-Cluster.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Не найден каталог решения для проверки публикации");
    }
}
