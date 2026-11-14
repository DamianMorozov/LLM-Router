namespace OpenLLMCluster.Infrastructure;

/// <summary>Этап загрузки и установки зависимости агента</summary>
public sealed record RuntimeInstallProgress(string Stage, int Percent = 0, string Package = "");

/// <summary>Подтверждённый результат штатного установщика Microsoft</summary>
public sealed record RuntimeInstallResult(string Version, bool RestartRequired);

/// <summary>Позволяет проверять UI без установки runtime в тестовой системе</summary>
public interface IAspNetRuntimeInstaller
{
    /// <summary>Скачивает подходящий runtime и запускает его установщик по команде пользователя</summary>
    Task<RuntimeInstallResult> InstallAsync(string architecture, IProgress<RuntimeInstallProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>Устанавливает согласованные .NET и ASP.NET Core Runtime 10 с Microsoft после проверки SHA-512</summary>
public sealed class AspNetRuntimeInstaller(HttpClient? client = null, string? downloadDirectory = null,
    Func<string, CancellationToken, Task<int>>? launchInstaller = null) : IAspNetRuntimeInstaller
{
    public const string MetadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json";
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly HttpClient _client = client ?? _http;
    private readonly string _downloadDirectory = downloadDirectory ?? Path.Combine(Path.GetTempPath(), "Open-LLM-Cluster", "Runtime");
    private readonly Func<string, CancellationToken, Task<int>> _launchInstaller = launchInstaller ?? LaunchInstallerAsync;

    /// <summary>Выбирает архитектуру, проверяет загрузку и обрабатывает отмену UAC и код перезагрузки</summary>
    public async Task<RuntimeInstallResult> InstallAsync(string architecture, IProgress<RuntimeInstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Установка этой кнопкой доступна только в Windows");
        if (architecture is not ("x64" or "x86" or "arm64")) throw new ArgumentException("Неподдерживаемая архитектура runtime", nameof(architecture));
        var installers = new List<(RuntimePackage Package, string Path)>();
        try
        {
            progress?.Report(new("RuntimeFinding"));
            var packages = await FindPackagesAsync(architecture, ct);
            foreach (var package in packages)
            {
                var path = Path.Combine(_downloadDirectory, Guid.NewGuid().ToString("N"), $"{package.Name}-{package.Version}-win-{architecture}.exe");
                installers.Add((package, path));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await DownloadAsync(package, path, progress, ct);
            }
            var restartRequired = false;
            foreach (var installer in installers)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new("RuntimeInstalling", Package: installer.Package.Name));
                var code = await _launchInstaller(installer.Path, ct);
                if (code is 1602 or 1223) throw new OperationCanceledException("Установка runtime отменена пользователем", ct);
                if (code is not (0 or 3010 or 1641)) throw new InvalidOperationException("Установщик " + installer.Package.Name + " завершился с кодом " + code);
                restartRequired |= code is 3010 or 1641;
            }
            return new(packages[0].Version, restartRequired);
        }
        catch (Exception ex) { Trace.TraceError("Install agent runtimes: {0}", ex.GetType().Name); throw; }
        finally { foreach (var installer in installers) DeleteDownloadedFile(installer.Path); }
    }

    // Выбирает последнюю стабильную пару: ASP.NET зависит от той же версии обычного .NET Runtime
    private async Task<RuntimePackage[]> FindPackagesAsync(string architecture, CancellationToken ct)
    {
        await using var stream = await _client.GetStreamAsync(MetadataUrl, ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        RuntimePackage[]? latest = null;
        foreach (var release in document.RootElement.GetProperty("releases").EnumerateArray())
        {
            if (!release.TryGetProperty("aspnetcore-runtime", out var aspnet) || !release.TryGetProperty("runtime", out var runtime)) continue;
            var versionText = aspnet.GetProperty("version").GetString();
            if (!Version.TryParse(versionText, out var version) || version.Major != 10 || version.Minor != 0) continue;
            if (runtime.GetProperty("version").GetString() != versionText || (latest is not null && version <= Version.Parse(latest[0].Version))) continue;
            var corePackage = FindInstaller(runtime, "dotnet-runtime", versionText!, architecture);
            var aspPackage = FindInstaller(aspnet, "aspnetcore-runtime", versionText!, architecture);
            if (corePackage is not null && aspPackage is not null) latest = [corePackage, aspPackage];
        }
        return latest ?? throw new InvalidDataException("В метаданных Microsoft не найдена согласованная пара .NET и ASP.NET Core Runtime 10");
    }

    // Проверяет официальный адрес и контрольную сумму установщика выбранной архитектуры
    private static RuntimePackage? FindInstaller(JsonElement runtime, string name, string version, string architecture)
    {
        foreach (var file in runtime.GetProperty("files").EnumerateArray())
        {
            if (file.GetProperty("rid").GetString() != "win-" + architecture
                || !file.GetProperty("name").GetString()!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var url = new Uri(file.GetProperty("url").GetString()!);
            if (url.Scheme != Uri.UriSchemeHttps || url.Host is not ("builds.dotnet.microsoft.com" or "download.visualstudio.microsoft.com"))
                throw new InvalidDataException("Установщик должен загружаться с официального HTTPS-сервера Microsoft");
            var hash = Convert.FromHexString(file.GetProperty("hash").GetString()!);
            if (hash.Length != 64) throw new InvalidDataException("В метаданных runtime отсутствует корректная SHA-512 сумма");
            return new(name, version, url, hash);
        }
        return null;
    }

    // Загружает установщик потоково и проверяет содержимое до запуска
    private async Task DownloadAsync(RuntimePackage package, string path, IProgress<RuntimeInstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("RuntimeDownloading", Package: package.Name));
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
                if (percent != lastPercent) { progress?.Report(new("RuntimeDownloading", percent, package.Name)); lastPercent = percent; }
            }
        }
        progress?.Report(new("RuntimeVerifying", Package: package.Name));
        await using var downloadedFile = File.OpenRead(path);
        var actual = await SHA512.HashDataAsync(downloadedFile, ct);
        if (!CryptographicOperations.FixedTimeEquals(actual, package.Hash))
            throw new InvalidDataException("Контрольная сумма установщика " + package.Name + " не совпала; файл не будет запущен");
    }

    // Показывает штатный установщик и UAC; после его запуска не прерывает системную установку отменой скачивания
    private static async Task<int> LaunchInstallerAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true, Verb = "runas", Arguments = "/install /passive /norestart"
            }) ?? throw new InvalidOperationException("Не удалось запустить установщик runtime");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Запрос UAC отменён", ex, ct); }
    }

    // Удаляет только загруженный этим экземпляром файл после завершения установщика или ошибки скачивания
    private static void DeleteDownloadedFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
        catch (Exception ex) { Trace.TraceWarning("Cleanup runtime download: {0}", ex.GetType().Name); }
    }

    private sealed record RuntimePackage(string Name, string Version, Uri Url, byte[] Hash);
}
