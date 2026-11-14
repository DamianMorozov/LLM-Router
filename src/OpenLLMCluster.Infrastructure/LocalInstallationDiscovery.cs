namespace OpenLLMCluster.Infrastructure;

/// <summary>Найденный каталог и наличие исполняемых файлов без подтверждения совместимости сборки</summary>
public sealed record RuntimeLocation(string Directory, bool HasServer, bool HasRpc)
{
    public string Label => "llama-server: " + (HasServer ? "✓" : "—") + " · ggml-rpc-server: " + (HasRpc ? "✓" : "—") + " · " + Directory;
}

/// <summary>Результаты локального поиска без загрузки моделей и запуска движка</summary>
public sealed record InstallationLocations(IReadOnlyList<RuntimeLocation> Runtimes, IReadOnlyList<string> ModelDirectories, bool HasReadErrors);

/// <summary>Ищет известные каталоги llama.cpp, PATH и настройки LM Studio без полного обхода дисков</summary>
public sealed class LocalInstallationDiscovery(string? userHome = null, string? applicationDirectory = null,
    string? searchPath = null, string? applicationData = null)
{
    private readonly string _home = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _application = applicationDirectory ?? AppContext.BaseDirectory;
    private readonly string _path = searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "";
    private readonly string _applicationData = applicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly StringComparer _comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Проверяет существование файлов в фоновом потоке; секреты из настроек не читаются в модель приложения</summary>
    public Task<InstallationLocations> FindAsync(string runtimeDirectory = "", string modelDirectory = "", CancellationToken ct = default)
        => Task.Run(() => Find(runtimeDirectory, modelDirectory, ct), ct);

    // Читает только каталог загрузок из известных настроек и проверяет известные расположения движка
    private InstallationLocations Find(string runtimeDirectory, string modelDirectory, CancellationToken ct)
    {
        var runtimes = new Dictionary<string, RuntimeLocation>(_comparer);
        var models = new List<string>();
        var readErrors = false;
        try
        {
            var studioRoots = new[] { Path.Combine(_home, ".lmstudio"), Path.Combine(_home, ".cache", "lm-studio"),
                Path.Combine(_applicationData, "LM Studio"), Path.Combine(_home, ".config", "LM Studio") }.Distinct(_comparer).ToArray();
            AddModelDirectory(modelDirectory, models);
            foreach (var root in studioRoots)
            {
                ct.ThrowIfCancellationRequested();
                var settings = Path.Combine(root, "settings.json");
                if (!File.Exists(settings)) continue;
                try
                {
                    using var stream = File.OpenRead(settings);
                    if (stream.Length == 0) continue;
                    using var document = JsonDocument.Parse(stream);
                    if (document.RootElement.TryGetProperty("downloadsFolder", out var folder) && folder.ValueKind == JsonValueKind.String)
                    {
                        var value = folder.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) AddModelDirectory(ExpandDirectory(value, root), models);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                { readErrors = true; Trace.TraceWarning("Чтение каталога моделей LM Studio: {0}", ex.GetType().Name); }
            }
            foreach (var root in studioRoots) AddModelDirectory(Path.Combine(root, "models"), models);
            AddRuntimeDirectory(runtimeDirectory, runtimes);
            AddRuntimeDirectory(_application, runtimes);
            foreach (var root in new[] { Path.Combine(_application, "runtime"), Path.Combine(_application, "llama.cpp") }
                .Concat(studioRoots.Select(root => Path.Combine(root, "extensions", "backends"))))
                ScanRuntimeRoot(root, runtimes, ref readErrors, ct);
            foreach (var directory in _path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                ct.ThrowIfCancellationRequested();
                AddRuntimeDirectory(directory.Trim().Trim('"'), runtimes);
            }
            return new(runtimes.Values.ToArray(), models, readErrors);
        }
        catch (Exception ex) { Trace.TraceError("Поиск локальных каталогов: {0}", ex.GetType().Name); throw; }
    }

    // Разрешает относительный путь относительно файла настроек, поддерживает домашний каталог и переменные окружения
    private string ExpandDirectory(string value, string settingsDirectory)
    {
        value = Environment.ExpandEnvironmentVariables(value);
        if (value == "~") return _home;
        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            value = Path.Combine(_home, value[2..]);
        return Path.GetFullPath(value, settingsDirectory);
    }

    // Добавляет только существующий каталог, не перечисляя и не читая веса моделей
    private static void AddModelDirectory(string directory, List<string> models)
    {
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            var fullPath = Path.GetFullPath(directory);
            if (!models.Contains(fullPath, _comparer)) models.Add(fullPath);
        }
    }

    // Проверяет оба исполняемых файла отдельно, не объявляя backend LM Studio совместимым с вычислительным RPC
    private static void AddRuntimeDirectory(string directory, Dictionary<string, RuntimeLocation> runtimes)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var hasServer = File.Exists(Path.Combine(directory, RuntimePlanner.GetExecutableName(NodeRole.Coordinator)));
        var hasRpc = File.Exists(Path.Combine(directory, RuntimePlanner.GetExecutableName(NodeRole.Worker)));
        if (hasServer || hasRpc) runtimes.TryAdd(Path.GetFullPath(directory), new(Path.GetFullPath(directory), hasServer, hasRpc));
    }

    // Ограничивает глубину известных каталогов и пропускает недоступные папки и символические ссылки
    private static void ScanRuntimeRoot(string root, Dictionary<string, RuntimeLocation> runtimes, ref bool readErrors, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            AddRuntimeDirectory(root, runtimes);
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3,
                IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var directory in Directory.EnumerateDirectories(root, "*", options).Order(_comparer))
            {
                ct.ThrowIfCancellationRequested();
                AddRuntimeDirectory(directory, runtimes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { readErrors = true; Trace.TraceWarning("Поиск каталога llama.cpp: {0}", ex.GetType().Name); }
    }
}
