// Проверяет команды нативной упаковки, отказ при неверном составе публикации и размеры изображений ICNS
namespace OpenLLMCluster.Tests;

public sealed class PlatformPackagingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "olc-pack-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Pack-Desktop.ps1")]
    [InlineData("Test-DesktopPackage.ps1")]
    public async Task ExecutablePermissionCommandUsesPortableArgumentsAndPreservesSpaces(string script)
    {
        Directory.CreateDirectory(_directory);
        var harness = """
            $ErrorActionPreference = 'Stop'
            $tokens = $null
            $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile($env:OLC_TEST_SCRIPT, [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw 'Неверный синтаксис скрипта' }
            $commands = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'chmod'
            }, $true))
            if ($commands.Count -ne 1) { throw 'Ожидается одна команда установки права запуска' }
            $payload = $env:OLC_TEST_ROOT
            $entry = 'Desktop with spaces'
            $appImage = Join-Path $payload $entry
            $command = $commands[0]
            $executable = $command.CommandElements[0].Extent
            $call = $command.Extent.Text.Remove($executable.StartOffset - $command.Extent.StartOffset, $executable.Text.Length)
            $call = $call.Insert($executable.StartOffset - $command.Extent.StartOffset, 'dotnet $env:OLC_TEST_FIXTURE --fixture-chmod')
            & ([scriptblock]::Create($call))
            if ($LASTEXITCODE -ne 0) { throw 'Нативный процесс отклонил аргументы chmod' }
            """;
        var start = new ProcessStartInfo("pwsh") { WorkingDirectory = RepositoryRoot(), RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["OLC_TEST_SCRIPT"] = Path.Combine(RepositoryRoot(), "tools", script);
        start.Environment["OLC_TEST_ROOT"] = Path.Combine(_directory, "publish with spaces");
        start.Environment["OLC_TEST_FIXTURE"] = Path.Combine(RepositoryRoot(), "tests", "OpenLLMCluster.EngineFixture", "bin",
            new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "OpenLLMCluster.EngineFixture.dll");
        foreach (var argument in new[] { "-NoProfile", "-Command", harness }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var output = await stdout;
        Assert.True(process.ExitCode == 0, output + await stderr);
        using var arguments = JsonDocument.Parse(output);
        Assert.Equal(new[] { "+x", Path.Combine(_directory, "publish with spaces", "Desktop with spaces") },
            arguments.RootElement.EnumerateArray().Select(value => value.GetString()).ToArray());
    }

    [Theory]
    [InlineData("win-x64", "--framework", "net10.0-x64-runtime,net10.0-x64-aspnetcore", ".ico")]
    [InlineData("linux-x64", "--compression", "gzip", ".png")]
    [InlineData("osx-arm64", "--bundleId", "io.github.damianmorozov.openllmcluster", ".icns")]
    [InlineData("osx-x64", "--bundleId", "io.github.damianmorozov.openllmcluster", ".icns")]
    public async Task NativePackagePlanIncludesAgentRuntimeChannelAndPlatformOptions(string rid, string option, string value, string iconExtension)
    {
        CreatePayload(rid);
        var result = await RunAsync(validateOnly: true);
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var plan = json.RootElement;
        var arguments = plan.GetProperty("Arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains(rid + "-stable", arguments);
        var index = Array.IndexOf(arguments, option);
        Assert.True(index >= 0, result.Output);
        Assert.Equal(value, arguments[index + 1]);
        Assert.EndsWith(iconExtension, arguments[Array.IndexOf(arguments, "--icon") + 1]);
        Assert.Contains("None", arguments);
        Assert.DoesNotContain("--signAppIdentity", arguments);
        if (rid.StartsWith("osx-", StringComparison.Ordinal)) Assert.Contains("--noInst", arguments);
        Assert.False(Directory.Exists(Path.Combine(_directory, "packages")));
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("channel")]
    [InlineData("version")]
    [InlineData("runtime")]
    [InlineData("private")]
    public async Task IncompleteOrInconsistentPayloadCannotBePackaged(string fault)
    {
        CreatePayload("win-x64");
        var manifestPath = Path.Combine(_directory, "publish", "publication.json");
        if (fault == "agent") File.Delete(Path.Combine(_directory, "publish", "Agent", "OpenLLMCluster.Agent.exe"));
        if (fault == "channel") File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("win-x64-stable", "linux-x64-stable"));
        if (fault == "version") File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("0.2.300", "9.9.9"));
        if (fault == "runtime") File.WriteAllText(Path.Combine(_directory, "publish", "hostfxr.dll"), "unexpected bundled runtime");
        if (fault == "private") File.WriteAllText(Path.Combine(_directory, "publish", "profile.json"), "fixture private profile");
        var result = await RunAsync(validateOnly: true);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(_directory, "packages")));
    }

    [Fact]
    public async Task WrongNativeHostIsRejectedBeforeCreatingOutput()
    {
        CreatePayload(OperatingSystem.IsMacOS() ? "win-x64" : "osx-arm64");
        var result = await RunAsync(validateOnly: false);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OLCPACK_HOST", result.Output);
        Assert.False(Directory.Exists(Path.Combine(_directory, "packages")));
    }

    [Fact]
    public void MacIconContainsValidPngRepresentationsUpToRetinaSize()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "src", "OpenLLMCluster.Desktop", "Assets", "open-llm-cluster.icns"));
        Assert.Equal("icns", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(bytes.Length, ReadBigEndian(bytes, 4));
        var dimensions = new Dictionary<string, int> { ["ic07"] = 128, ["ic08"] = 256, ["ic09"] = 512, ["ic10"] = 1024 };
        var offset = 8;
        while (offset < bytes.Length)
        {
            var type = Encoding.ASCII.GetString(bytes, offset, 4);
            var length = ReadBigEndian(bytes, offset + 4);
            Assert.True(length > 32 && offset + length <= bytes.Length);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[(offset + 8)..(offset + 16)]);
            Assert.Equal(dimensions[type], ReadBigEndian(bytes, offset + 24));
            Assert.Equal(dimensions[type], ReadBigEndian(bytes, offset + 28));
            dimensions.Remove(type);
            offset += length;
        }
        Assert.Empty(dimensions);
        Assert.Equal(bytes.Length, offset);
    }

    // Готовит минимальный состав framework-dependent публикации для проверки до запуска vpk
    private void CreatePayload(string rid)
    {
        var directory = Path.Combine(_directory, "publish");
        Directory.CreateDirectory(Path.Combine(directory, "Agent"));
        var extension = rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "";
        foreach (var name in new[] { "OpenLLMCluster.Desktop", "OpenLLMCluster.Agent" })
        {
            var component = name.EndsWith("Agent", StringComparison.Ordinal) ? Path.Combine(directory, "Agent") : directory;
            File.WriteAllBytes(Path.Combine(component, name + extension), [1]);
            File.WriteAllBytes(Path.Combine(component, name + ".dll"), [1]);
            File.WriteAllText(Path.Combine(component, name + ".deps.json"), JsonSerializer.Serialize(new
            {
                runtimeTarget = new { name = ".NETCoreApp,Version=v10.0/" + rid },
                libraries = new Dictionary<string, object> { [name + "/0.2.300"] = new { } }
            }));
            var frameworks = new List<object> { new { name = "Microsoft.NETCore.App", version = "10.0.0" } };
            if (component != directory) frameworks.Add(new { name = "Microsoft.AspNetCore.App", version = "10.0.0" });
            File.WriteAllText(Path.Combine(component, name + ".runtimeconfig.json"),
                JsonSerializer.Serialize(new { runtimeOptions = new { frameworks } }));
        }
        File.WriteAllText(Path.Combine(directory, "publication.json"), JsonSerializer.Serialize(new
        {
            PackId = "OpenLLMCluster", MacOSBundleId = "io.github.damianmorozov.openllmcluster", Version = "0.2.300", PackageVersion = "0.2.300",
            ReleaseTag = "v0.2.300", RuntimeIdentifier = rid, Channel = rid + "-stable", VelopackVersion = "1.2.161", SelfContained = false,
            RequiredFrameworks = new[] { "Microsoft.NETCore.App/10.0", "Microsoft.AspNetCore.App/10.0" },
            WindowsRuntimeDependencies = rid == "win-x64" ? "net10.0-x64-runtime,net10.0-x64-aspnetcore" : "",
            Desktop = "OpenLLMCluster.Desktop" + extension, Agent = "Agent/OpenLLMCluster.Agent" + extension
        }));
    }

    // Запускает настоящий скрипт с аргументами без shell-интерполяции
    private async Task<(int ExitCode, string Output)> RunAsync(bool validateOnly)
    {
        var start = new ProcessStartInfo("pwsh") { WorkingDirectory = RepositoryRoot(), RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-File", "tools/Pack-Desktop.ps1", "-PublishDirectory", Path.Combine(_directory, "publish"),
            "-OutputDirectory", Path.Combine(_directory, "packages") }) start.ArgumentList.Add(argument);
        if (validateOnly) start.ArgumentList.Add("-ValidateOnly");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }

    // Читает размер блока ICNS или PNG в сетевом порядке байтов
    private static int ReadBigEndian(byte[] bytes, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));

    // Находит каталог решения из каталога тестового процесса
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Open-LLM-Cluster.slnx"))) directory = directory.Parent;
        return directory!.FullName;
    }

    /// <summary>Удаляет только подготовленные тестом файлы</summary>
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
