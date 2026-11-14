// Проверяет подготовку полного набора релиза, согласование тега и отказ до обращения к GitHub при повреждённых пакетах
namespace OpenLLMCluster.Tests;

public sealed class ReleaseWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "olc-release-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] _rids = ["win-x64", "linux-x64", "osx-arm64", "osx-x64"];

    [Fact]
    public async Task ReleasePlanValidatesVersionAndWritesSafeGithubOutputs()
    {
        Directory.CreateDirectory(_directory);
        var output = Path.Combine(_directory, "outputs.txt");
        var result = await RunAsync("Get-ReleasePlan.ps1", "-ReleaseTag", "v0.2.300", "-Channel", "stable", "-GitHubOutputPath", output);
        Assert.Equal(0, result.ExitCode);
        using var plan = JsonDocument.Parse(result.Output);
        Assert.Equal("0.2.300", plan.RootElement.GetProperty("Version").GetString());
        Assert.Equal("stable", plan.RootElement.GetProperty("Channel").GetString());
        Assert.Matches("^[0-9a-f]{40}$", plan.RootElement.GetProperty("Commit").GetString()!);
        Assert.Contains("tag=v0.2.300", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("v9.9.9")]
    [InlineData("main")]
    [InlineData("v0.2.300\ncommit=other")]
    public async Task InvalidTagCannotCreateOutputs(string tag)
    {
        var output = Path.Combine(_directory, "outputs.txt");
        var result = await RunAsync("Get-ReleasePlan.ps1", "-ReleaseTag", tag, "-GitHubOutputPath", output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CompleteBundleIncludesAllChannelsAndOneChecksumFileWithoutPayloadOrSecrets()
    {
        CreatePackages();
        var result = await BundleAsync();
        Assert.Equal(0, result.ExitCode);
        var output = Path.Combine(_directory, "bundle");
        var files = Directory.GetFiles(output).Select(Path.GetFileName).ToArray();
        Assert.Equal(22, files.Length);
        Assert.Contains("SHA256SUMS", files);
        foreach (var rid in _rids) Assert.Contains($"releases.{rid}-stable.json", files);
        Assert.DoesNotContain(files, name => name!.Contains("publication", StringComparison.Ordinal));
        var checksumLines = File.ReadAllLines(Path.Combine(output, "SHA256SUMS"));
        Assert.Equal(21, checksumLines.Length);
        foreach (var line in checksumLines)
        {
            var parts = line.Split("  ", 2);
            Assert.Equal(parts[0], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, parts[1])))).ToLowerInvariant());
        }
        using var plan = JsonDocument.Parse(result.Output);
        Assert.True(plan.RootElement.GetProperty("Draft").GetBoolean());
    }

    [Theory]
    [InlineData("missing-platform")]
    [InlineData("corrupt-package")]
    [InlineData("wrong-channel")]
    [InlineData("private-file")]
    [InlineData("missing-portable")]
    public async Task InvalidBundleDoesNotCreateAnyReleaseOutput(string fault)
    {
        CreatePackages();
        var windows = Path.Combine(_directory, "input", "packages-win-x64");
        if (fault == "missing-platform") Directory.Delete(Path.Combine(_directory, "input", "packages-osx-x64"), recursive: true);
        if (fault == "corrupt-package") File.AppendAllText(Path.Combine(windows, "OpenLLMCluster-0.2.300-win-x64-stable-full.nupkg"), "bad");
        if (fault == "wrong-channel") File.Move(Path.Combine(windows, "releases.win-x64-stable.json"), Path.Combine(windows, "releases.win-x64-beta.json"));
        if (fault == "private-file") File.WriteAllText(Path.Combine(windows, "profile.json"), "private fixture");
        if (fault == "missing-portable") File.Delete(Path.Combine(windows, "OpenLLMCluster-win-x64-stable-Portable.zip"));
        var result = await BundleAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(_directory, "bundle")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualApiFlowCreatesOrUpdatesOnlyDraftAndUploadsExactlyVerifiedBytes(bool existingDraft)
    {
        CreatePackages();
        await using var api = await ReleaseApiFixture.StartAsync(await CurrentCommitAsync(), existingDraft ? "draft" : "new");
        var result = await PublishAsync(api);
        Assert.Equal(0, result.ExitCode);
        Assert.True(api.Draft);
        Assert.Equal(22, api.Uploads.Count);
        Assert.Equal(existingDraft ? 1 : 0, api.Deletes);
        Assert.Equal(1, api.Mutations);
        Assert.Contains($"https://github.com/fixture/olc/blob/{await CurrentCommitAsync()}/README-DEV.md", api.Notes);
        foreach (var (name, bytes) in api.Uploads)
            Assert.Equal(File.ReadAllBytes(Path.Combine(_directory, "bundle", name)), bytes);
        Assert.False(api.WrongToken);
    }

    [Theory]
    [InlineData("published")]
    [InlineData("wrong-commit")]
    public async Task PublishedReleaseOrDifferentCommitCannotBeModified(string mode)
    {
        CreatePackages();
        await using var api = await ReleaseApiFixture.StartAsync(await CurrentCommitAsync(), mode);
        var result = await PublishAsync(api);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(mode == "published" ? "уже опубликован" : "другому коммиту", result.Output);
        Assert.Equal(0, api.Mutations);
        Assert.Equal(0, api.Deletes);
        Assert.Empty(api.Uploads);
    }

    [Fact]
    public async Task UploadFailureNeverPublishesPartialDraft()
    {
        CreatePackages();
        await using var api = await ReleaseApiFixture.StartAsync(await CurrentCommitAsync(), "fail-upload");
        var result = await PublishAsync(api);
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(api.Draft);
        Assert.Equal(1, api.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BaselineAllowsFirstReleaseButDoesNotHideApiFailure(bool fail)
    {
        await using var api = await ReleaseApiFixture.StartAsync(await CurrentCommitAsync(), fail ? "fail-baseline" : "new");
        var packageDirectory = Path.Combine(_directory, "baseline");
        var result = await RunWithEnvironmentAsync("Restore-ReleaseBaseline.ps1",
            new Dictionary<string, string> { ["GH_TOKEN"] = "fixture-token" }, "-PackageDirectory", packageDirectory,
            "-RuntimeIdentifier", "win-x64", "-Repository", "fixture/olc", "-ApiUrl", api.Url);
        if (fail)
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("503", result.Output);
        }
        else
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Первый выпуск канала win-x64-stable", result.Output);
        }
        Assert.True(api.Reads > 0);
        Assert.Equal(0, api.Mutations);
        Assert.False(api.WrongToken);
        Assert.False(Directory.Exists(packageDirectory));
    }

    // Создаёт минимальный комплект из четырёх платформ с настоящими контрольными суммами
    private void CreatePackages()
    {
        foreach (var rid in _rids)
        {
            var directory = Path.Combine(_directory, "input", "packages-" + rid);
            Directory.CreateDirectory(Path.Combine(directory, "publish"));
            File.WriteAllText(Path.Combine(directory, "publish", "publication.json"), JsonSerializer.Serialize(new
            {
                PackId = "OpenLLMCluster", Version = "0.2.300", ReleaseTag = "v0.2.300", RuntimeIdentifier = rid, Channel = rid + "-stable", SelfContained = false
            }));
            var package = $"OpenLLMCluster-0.2.300-{rid}-stable-full.nupkg";
            File.WriteAllText(Path.Combine(directory, package), "package fixture " + rid);
            var bytes = File.ReadAllBytes(Path.Combine(directory, package));
            File.WriteAllText(Path.Combine(directory, $"releases.{rid}-stable.json"), JsonSerializer.Serialize(new { Assets = new[] { new
            {
                PackageId = "OpenLLMCluster", Version = "0.2.300", Type = "Full", FileName = package,
                Size = bytes.Length, SHA256 = Convert.ToHexString(SHA256.HashData(bytes))
            } } }));
            File.WriteAllText(Path.Combine(directory, $"assets.{rid}-stable.json"), "{}");
            File.WriteAllText(Path.Combine(directory, $"RELEASES-{rid}-stable"), "fixture legacy index");
            var portable = rid == "linux-x64" ? $"OpenLLMCluster-{rid}-stable.AppImage" : $"OpenLLMCluster-{rid}-stable-Portable.zip";
            File.WriteAllText(Path.Combine(directory, portable), "portable fixture");
            if (rid == "win-x64") File.WriteAllText(Path.Combine(directory, "OpenLLMCluster-win-x64-stable-Setup.exe"), "setup fixture");
            var sums = Directory.GetFiles(directory).Select(file =>
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant() + "  " + Path.GetFileName(file));
            File.WriteAllLines(Path.Combine(directory, "SHA256SUMS"), sums);
        }
    }

    // Проверяет комплект без токена и без сетевых запросов
    private Task<(int ExitCode, string Output)> BundleAsync() => RunAsync("Publish-ReleaseDraft.ps1", "-InputDirectory", Path.Combine(_directory, "input"),
        "-OutputDirectory", Path.Combine(_directory, "bundle"), "-ReleaseTag", "v0.2.300", "-Channel", "stable", "-ValidateOnly");

    // Передаёт фиктивный токен только изолированному loopback-серверу
    private Task<(int ExitCode, string Output)> PublishAsync(ReleaseApiFixture api) => RunWithEnvironmentAsync("Publish-ReleaseDraft.ps1",
        new Dictionary<string, string> { ["GH_TOKEN"] = "fixture-token" }, "-InputDirectory", Path.Combine(_directory, "input"),
        "-OutputDirectory", Path.Combine(_directory, "bundle"), "-ReleaseTag", "v0.2.300", "-Repository", "fixture/olc", "-ApiUrl", api.Url);

    // Читает настоящий HEAD без изменения Git и без создания тестовых коммитов
    private static async Task<string> CurrentCommitAsync()
    {
        var result = await RunAsync("Get-ReleasePlan.ps1", "-ReleaseTag", "v0.2.300");
        using var json = JsonDocument.Parse(result.Output);
        return json.RootElement.GetProperty("Commit").GetString()!;
    }

    // Выполняет настоящий PowerShell-скрипт без вставки аргументов в командную строку оболочки
    private static Task<(int ExitCode, string Output)> RunAsync(string script, params string[] arguments) =>
        RunWithEnvironmentAsync(script, new Dictionary<string, string>(), arguments);

    // Изолирует окружение публикации от пользовательских токенов и выполняет тестируемый процесс
    private static async Task<(int ExitCode, string Output)> RunWithEnvironmentAsync(string script,
        IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh") { WorkingDirectory = RepositoryRoot(), RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Remove("GH_TOKEN");
        start.Environment.Remove("GITHUB_TOKEN");
        foreach (var (key, value) in environment) start.Environment[key] = value;
        foreach (var argument in new[] { "-NoProfile", "-File", "tools/" + script }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }

    // Находит корень решения из каталога тестового процесса
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Open-LLM-Cluster.slnx"))) directory = directory.Parent;
        return directory!.FullName;
    }

    /// <summary>Удаляет только созданный тестом временный каталог</summary>
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    // Принимает настоящие HTTP-запросы скрипта; внешние GitHub-серверы в тестах не используются
    private sealed class ReleaseApiFixture(string commit, string mode) : IAsyncDisposable
    {
        private WebApplication? _application;
        public string Url { get; private set; } = "";
        public bool Draft { get; private set; } = mode != "published";
        public bool WrongToken { get; private set; }
        public int Mutations { get; private set; }
        public int Reads { get; private set; }
        public int Deletes { get; private set; }
        public string Notes { get; private set; } = "";
        public Dictionary<string, byte[]> Uploads { get; } = [];

        /// <summary>Запускает изолированный HTTP API на автоматически выбранном порту</summary>
        public static async Task<ReleaseApiFixture> StartAsync(string commit, string mode)
        {
            var fixture = new ReleaseApiFixture(commit, mode);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            fixture._application = builder.Build();
            fixture._application.Run(fixture.HandleAsync);
            await fixture._application.StartAsync(TestContext.Current.CancellationToken);
            var addresses = fixture._application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            fixture.Url = Assert.Single(addresses);
            return fixture;
        }

        // Возвращает выпуск без выполнения каких-либо операций с настоящим GitHub
        private object Release() => new { id = 7, tag_name = "v0.2.300", draft = Draft,
            target_commitish = mode == "wrong-commit" ? new string('a', 40) : commit,
            upload_url = Url + "/upload{?name,label}", html_url = "https://example.invalid/draft" };

        // Проверяет состояние черновика и записывает полученные байты каждого ассета
        private async Task HandleAsync(HttpContext context)
        {
            if (context.Request.Headers.Authorization.ToString() != "Bearer fixture-token") WrongToken = true;
            var path = context.Request.Path.Value!;
            if (context.Request.Method == "GET" && path.EndsWith("/releases", StringComparison.Ordinal))
            {
                Reads++;
                if (mode == "fail-baseline") { context.Response.StatusCode = 503; return; }
                await context.Response.WriteAsJsonAsync(mode is "new" or "fail-upload" ? [] : new[] { Release() },
                    TestContext.Current.CancellationToken);
                return;
            }
            if (context.Request.Method == "GET" && path.EndsWith("/assets", StringComparison.Ordinal))
            {
                await context.Response.WriteAsJsonAsync(new[] { new { id = 9 } }, TestContext.Current.CancellationToken);
                return;
            }
            if (context.Request.Method == "DELETE") { Deletes++; context.Response.StatusCode = 204; return; }
            if (path == "/upload")
            {
                if (mode == "fail-upload") { context.Response.StatusCode = 503; return; }
                using var bytes = new MemoryStream();
                await context.Request.Body.CopyToAsync(bytes, TestContext.Current.CancellationToken);
                Uploads.Add(context.Request.Query["name"].ToString(), bytes.ToArray());
                await context.Response.WriteAsJsonAsync(new { id = Uploads.Count }, TestContext.Current.CancellationToken);
                return;
            }
            if (context.Request.Method is "POST" or "PATCH")
            {
                using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: TestContext.Current.CancellationToken);
                Draft = json.RootElement.GetProperty("draft").GetBoolean();
                Notes = json.RootElement.GetProperty("body").GetString()!;
                Assert.True(Draft);
                Assert.Equal("false", json.RootElement.GetProperty("make_latest").GetString());
                Assert.Equal(commit, json.RootElement.GetProperty("target_commitish").GetString());
                Mutations++;
                await context.Response.WriteAsJsonAsync(Release(), TestContext.Current.CancellationToken);
                return;
            }
            context.Response.StatusCode = 404;
        }

        /// <summary>Останавливает только тестовый HTTP-сервер</summary>
        public async ValueTask DisposeAsync() { if (_application is not null) await _application.DisposeAsync(); }
    }
}
