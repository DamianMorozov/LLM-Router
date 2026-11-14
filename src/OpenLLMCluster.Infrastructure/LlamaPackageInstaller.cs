namespace OpenLLMCluster.Infrastructure;

/// <summary>Прогресс установки управляемой сборки</summary>
public sealed record LlamaInstallProgress(string Stage, string File = "", int Percent = 0);

/// <summary>Позволяет проверять интерфейс без загрузки системных пакетов</summary>
public interface ILlamaPackageInstaller
{
    /// <summary>Устанавливает официальные пакеты одной закреплённой версии с проверкой SHA-256</summary>
    Task<LlamaInstallation> InstallAsync(string tag, LlamaBackend backend, IProgress<LlamaInstallProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>Скачивает llama.cpp и CUDA-библиотеки из одного релиза, проверяет RPC и сохраняет отдельный каталог версии</summary>
public sealed class LlamaPackageInstaller(HttpClient? client = null, LlamaInstallationStore? store = null,
    Func<string, CancellationToken, Task<string>>? validateBuild = null) : ILlamaPackageInstaller
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly HttpClient _client = client ?? _http;
    private readonly LlamaInstallationStore _store = store ?? new();
    private readonly Func<string, CancellationToken, Task<string>> _validateBuild = validateBuild ?? new EngineCommandRunner().ValidateBuildAsync;

    /// <summary>Публикует каталог только после проверки обоих исполняемых файлов и возможностей CLI</summary>
    public async Task<LlamaInstallation> InstallAsync(string tag, LlamaBackend backend, IProgress<LlamaInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        ValidateTag(tag);
        var platform = GetPlatform();
        var names = GetAssetNames(tag, backend, platform);
        var destination = Path.GetFullPath(Path.Combine(_store.Root, tag + "-" + platform + "-" + backend.ToString().ToLowerInvariant()));
        var staging = Path.GetFullPath(Path.Combine(_store.Root, ".install-" + Guid.NewGuid().ToString("N")));
        try
        {
            if (Directory.Exists(destination))
            {
                var existingDirectory = FindExecutableDirectory(destination);
                var existing = LlamaInstallationStore.ReadDirectory(existingDirectory)
                    ?? throw new InvalidDataException("Каталог версии уже существует без манифеста Open-LLM-Cluster; выбери другой каталог или версию");
                if (existing.ReleaseTag != tag || existing.Backend != backend) throw new InvalidDataException("Манифест сборки не соответствует выбору");
                var existingVersion = await _validateBuild(existingDirectory, ct);
                RequireBuildVersion(existingVersion, tag);
                _store.Save(existing);
                return existing;
            }
            progress?.Report(new("LlamaMetadata"));
            var packages = await ReadReleaseAsync(tag, names, ct);
            var payload = Path.Combine(staging, "payload");
            Directory.CreateDirectory(payload);
            foreach (var package in packages)
            {
                var archive = Path.Combine(staging, package.Name);
                await DownloadAsync(package, archive, progress, ct);
                progress?.Report(new("LlamaExtracting", package.Name));
                if (archive.EndsWith(".zip", StringComparison.Ordinal)) ZipFile.ExtractToDirectory(archive, payload, overwriteFiles: true);
                else
                {
                    await using var file = File.OpenRead(archive);
                    await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                    await TarFile.ExtractToDirectoryAsync(gzip, payload, overwriteFiles: true, cancellationToken: ct);
                }
                ct.ThrowIfCancellationRequested();
            }
            var executableDirectory = FindExecutableDirectory(payload);
            progress?.Report(new("LlamaVerifying"));
            var version = await _validateBuild(executableDirectory, ct);
            RequireBuildVersion(version, tag);
            var relative = Path.GetRelativePath(payload, executableDirectory);
            EnsureInsideRoot(payload);
            EnsureInsideRoot(destination);
            Directory.Move(payload, destination);
            var installed = new LlamaInstallation(tag, backend, platform, Path.GetFullPath(Path.Combine(destination, relative)), version);
            _store.Save(installed);
            return installed;
        }
        catch (Exception ex) { Trace.TraceError("Установка llama.cpp: {0}", ex.GetType().Name); throw; }
        finally
        {
            try { EnsureInsideRoot(staging); if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception ex) { Trace.TraceWarning("Очистка загрузки llama.cpp: {0}", ex.GetType().Name); }
        }
    }

    /// <summary>Принимает только числовые официальные build-теги, не позволяя подменять URL и пути</summary>
    public static void ValidateTag(string tag)
    {
        if (tag.Length is < 2 or > 20 || tag[0] != 'b' || !tag[1..].All(char.IsAsciiDigit))
            throw new ArgumentException("Укажи тег официальной сборки, например " + LlamaBuildDefaults.ReleaseTag);
    }

    /// <summary>Сравнивает полный номер build из ответа CLI с выбранным релизом</summary>
    public static void RequireBuildVersion(string version, string tag)
    {
        ValidateTag(tag);
        var words = version.Split([' ', '\r', '\n', '\t', ',', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < words.Length; index++)
            if (words[index].Equals("build", StringComparison.OrdinalIgnoreCase) && words[index + 1] == tag[1..]) return;
        throw new InvalidDataException("Версия исполняемого файла не соответствует закреплённому релизу " + tag);
    }

    /// <summary>Возвращает идентификатор текущей платформы</summary>
    public static string GetPlatform()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "ubuntu" : OperatingSystem.IsMacOS() ? "macos" : "unknown";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        if (os == "unknown" || architecture is not ("x64" or "arm64")) throw new PlatformNotSupportedException("Нет пакета для текущей ОС или архитектуры");
        return os + "-" + architecture;
    }

    /// <summary>Выбирает вычислительный пакет и отдельные CUDA-библиотеки из того же релиза</summary>
    public static string[] GetAssetNames(string tag, LlamaBackend backend, string platform)
    {
        ValidateTag(tag);
        return (platform, backend) switch
        {
            ("win-x64", LlamaBackend.Cuda) => [$"llama-{tag}-bin-win-cuda-12.4-x64.zip", "cudart-llama-bin-win-cuda-12.4-x64.zip"],
            ("win-arm64", LlamaBackend.Cuda) => [$"llama-{tag}-bin-win-cuda-13.5-arm64.zip", "cudart-llama-bin-win-cuda-13.5-arm64.zip"],
            ("win-x64", LlamaBackend.Cpu) => [$"llama-{tag}-bin-win-cpu-x64.zip"],
            ("win-arm64", LlamaBackend.Cpu) => [$"llama-{tag}-bin-win-cpu-arm64.zip"],
            ("ubuntu-x64", LlamaBackend.Cuda) => [$"llama-{tag}-bin-ubuntu-cuda-12.8-x64.tar.gz", $"cudart-llama-{tag}-bin-ubuntu-cuda-12.8-x64.tar.gz"],
            ("ubuntu-arm64", LlamaBackend.Cuda) => [$"llama-{tag}-bin-ubuntu-cuda-13.5-arm64.tar.gz", $"cudart-llama-{tag}-bin-ubuntu-cuda-13.5-arm64.tar.gz"],
            ("ubuntu-x64", LlamaBackend.Cpu) => [$"llama-{tag}-bin-ubuntu-x64.tar.gz"],
            ("ubuntu-arm64", LlamaBackend.Cpu) => [$"llama-{tag}-bin-ubuntu-arm64.tar.gz"],
            ("macos-arm64", LlamaBackend.Metal or LlamaBackend.Cpu) => [$"llama-{tag}-bin-macos-arm64.tar.gz"],
            ("macos-x64", LlamaBackend.Metal or LlamaBackend.Cpu) => [$"llama-{tag}-bin-macos-x64.tar.gz"],
            _ => throw new PlatformNotSupportedException("Для выбранного ускорителя нет пакета на текущей платформе")
        };
    }

    // Читает конкретный тег, не используя /latest: все компьютеры получают одинаковый build
    private async Task<Package[]> ReadReleaseAsync(string tag, string[] names, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/" + tag);
        request.Headers.UserAgent.ParseAdd("Open-LLM-Cluster/0.2");
        using var response = await _client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.GetProperty("tag_name").GetString() != tag) throw new InvalidDataException("GitHub вернул другой релиз");
        var packages = new List<Package>();
        foreach (var name in names)
        {
            var found = document.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(asset => asset.GetProperty("name").GetString() == name);
            if (found.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("В релизе отсутствует пакет " + name);
            var url = found.GetProperty("browser_download_url").GetString();
            if (url != "https://github.com/ggml-org/llama.cpp/releases/download/" + tag + "/" + name)
                throw new InvalidDataException("Пакет должен загружаться из официального релиза llama.cpp");
            var digest = found.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)) throw new InvalidDataException("GitHub не предоставил SHA-256 пакета");
            var hash = Convert.FromHexString(digest[7..]);
            if (hash.Length != 32) throw new InvalidDataException("Некорректная SHA-256 пакета");
            packages.Add(new(name, new Uri(url!), hash));
        }
        return packages.ToArray();
    }

    // Загружает архив потоково с возможностью отмены; распаковка начинается после проверки хеша
    private async Task DownloadAsync(Package package, string path, IProgress<LlamaInstallProgress>? progress, CancellationToken ct)
    {
        using var response = await _client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(path))
        {
            var buffer = new byte[81920];
            var total = response.Content.Headers.ContentLength;
            long downloaded = 0;
            var lastPercent = -1;
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                downloaded += count;
                var percent = total is > 0 ? (int)Math.Min(100, downloaded * 100 / total.Value) : 0;
                if (percent != lastPercent) { progress?.Report(new("LlamaDownloading", package.Name, percent)); lastPercent = percent; }
            }
        }
        await using var file = File.OpenRead(path);
        if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, ct), package.Hash))
            throw new InvalidDataException("SHA-256 не совпала для " + package.Name + "; архив не будет распакован");
    }

    // Находит bin внутри архива, сохраняя структуру зависимостей Linux и macOS
    private static string FindExecutableDirectory(string root)
    {
        var executable = Directory.EnumerateFiles(root, RuntimePlanner.GetExecutableName(NodeRole.Coordinator), SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidDataException("В пакете отсутствует llama-server");
        var directory = Path.GetDirectoryName(executable)!;
        if (!File.Exists(Path.Combine(directory, RuntimePlanner.GetExecutableName(NodeRole.Worker))))
            throw new InvalidDataException("В пакете отсутствует ggml-rpc-server; такая сборка не подходит для кластера");
        return directory;
    }

    // Ограничивает перемещение и удаление собственным каталогом установки
    private void EnsureInsideRoot(string path)
    {
        var root = Path.GetFullPath(_store.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFullPath(path).StartsWith(root, comparison)) throw new InvalidOperationException("Каталог установки выходит за пределы хранилища");
    }

    private sealed record Package(string Name, Uri Url, byte[] Hash);
}
