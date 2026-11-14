namespace OpenLLMCluster.Infrastructure;

/// <summary>Диапазон сырых весов одного тензора внутри локального GGUF</summary>
public sealed record GgufTensorRange(long Offset, long Length);

/// <summary>Подготавливает совместимый с закреплённой сборкой дисковый кэш RPC из локального GGUF</summary>
public sealed class RpcModelCache(string? root = null, Func<string, string, IReadOnlyList<GgufTensorRange>>? readLayout = null)
{
    private const long HashThreshold = 10 * 1024 * 1024;
    private readonly Func<string, string, IReadOnlyList<GgufTensorRange>> _readLayout = readLayout ?? NativeGgufLayout.Read;
    public string CacheRoot { get; } = root ?? DefaultCacheRoot;
    public static string DefaultCacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Open-LLM-Cluster", "rpc-cache", LlamaBuildDefaults.ReleaseTag);

    /// <summary>Вычисляет размер и SHA-256 каждой части GGUF, не передавая её содержимое по сети</summary>
    public static async Task<LocalModelIdentity> IdentifyAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var identity = new LocalModelIdentity();
            foreach (var file in ResolveFiles(path))
            {
                ct.ThrowIfCancellationRequested();
                await using var stream = OpenModel(file);
                identity.Files.Add(new() { Length = stream.Length, Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) });
            }
            return identity;
        }
        catch (Exception ex) { Trace.TraceError("Отпечаток модели: {0}", ex.GetType().Name); throw; }
    }

    /// <summary>Проверяет идентичность до записи тензоров; путь выбирается только локально</summary>
    public async Task<LocalModelStatus> PrepareAsync(string path, string runtimeDirectory, LocalModelIdentity? expected = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report("LocalModelHashing");
            var identity = await IdentifyAsync(path, ct).ConfigureAwait(false);
            if (expected is not null) RequireMatch(expected, identity);
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity))));
            var marker = Path.Combine(CacheRoot, "manifests", key + ".json");
            var previous = ReadMarker(marker);
            if (previous is not null && previous.All(entry => IsCurrent(entry))) return Status(identity, previous);
            var entries = new List<CacheEntry>();
            progress?.Report("LocalModelPreparing");
            foreach (var file in ResolveFiles(path))
            {
                await using var stream = OpenModel(file);
                foreach (var tensor in _readLayout(file, runtimeDirectory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (tensor.Offset < 0 || tensor.Length <= 0 || tensor.Offset > stream.Length || tensor.Length > stream.Length - tensor.Offset)
                        throw new InvalidDataException("Диапазон тензора выходит за границы GGUF");
                    if (tensor.Length <= HashThreshold) continue;
                    entries.Add(await WriteTensorAsync(stream, tensor, ct).ConfigureAwait(false));
                }
            }
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(entries), ct).ConfigureAwait(false);
            return Status(identity, entries);
        }
        catch (Exception ex) { Trace.TraceError("Локальный кэш модели: {0}", ex.GetType().Name); throw; }
    }

    /// <summary>Запрещает загрузку при различии количества частей, размеров или SHA-256</summary>
    public static void RequireMatch(LocalModelIdentity expected, LocalModelIdentity actual)
    {
        if (expected.Files is null || expected.Files.Count == 0 || expected.Files.Count != actual.Files.Count)
            throw new InvalidDataException("Не совпадает количество частей локальной модели и модели главного ПК");
        for (var index = 0; index < expected.Files.Count; index++)
        {
            var source = expected.Files[index];
            var local = actual.Files[index];
            if (source.Length <= 0 || source.Sha256.Length != 64 || !source.Sha256.All(Uri.IsHexDigit)
                || source.Length != local.Length || !string.Equals(source.Sha256, local.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Не совпадают размер или SHA-256 локальной модели и модели главного ПК: часть " + (index + 1));
        }
    }

    // Определяет все части стандартного split-GGUF; порядок частей не зависит от сортировки файловой системы
    private static IReadOnlyList<string> ResolveFiles(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Укажи полный локальный путь к GGUF");
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Выбери файл GGUF");
        var stem = name[..^5];
        if (stem.Length < 15 || stem[^15] != '-' || stem.Substring(stem.Length - 9, 4) != "-of-") return [path];
        if (!int.TryParse(stem.AsSpan(stem.Length - 14, 5), out var part) || !int.TryParse(stem.AsSpan(stem.Length - 5), out var count)) return [path];
        if (part != 1 || count is < 1 or > 99999) throw new InvalidDataException("Для составного GGUF выбери первую часть 00001");
        return Enumerable.Range(1, count).Select(index => Path.Combine(Path.GetDirectoryName(path)!, stem[..^15] + $"-{index:00000}-of-{count:00000}.gguf"))
            .ToArray();
    }

    // Проверяет сигнатуру до вычисления отпечатка и удерживает файл открытым без разрешения записи
    private static FileStream OpenModel(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != 8 || !header[..4].SequenceEqual("GGUF"u8) || BitConverter.ToUInt32(header[4..]) is not (2 or 3))
        { stream.Dispose(); throw new InvalidDataException("Файл не содержит корректный заголовок GGUF"); }
        stream.Position = 0;
        return stream;
    }

    // Сохраняет сырые байты и имя FNV-1a в точном формате ggml-rpc b12318; SHA-256 проверяется отдельно для всего GGUF
    private async Task<CacheEntry> WriteTensorAsync(FileStream source, GgufTensorRange tensor, CancellationToken ct)
    {
        var directory = Path.Combine(CacheRoot, "rpc");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var hash = 0xcbf29ce484222325UL;
            source.Position = tensor.Offset;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[1024 * 1024];
                for (var remaining = tensor.Length; remaining > 0;)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("GGUF закончился внутри тензора");
                    hash = Hash(buffer.AsSpan(0, read), hash);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    remaining -= read;
                }
            }
            ct.ThrowIfCancellationRequested();
            var name = hash.ToString("x16");
            var destination = Path.Combine(directory, name);
            File.Move(temporary, destination, overwrite: true);
            return new(name, tensor.Length, File.GetLastWriteTimeUtc(destination).Ticks);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Вычисляет протокольный FNV-1a с намеренным переполнением UInt64
    private static ulong Hash(ReadOnlySpan<byte> data, ulong hash)
    {
        foreach (var value in data) hash = unchecked((hash ^ value) * 0x100000001b3UL);
        return hash;
    }

    // Повреждённый маркер перестраивается с локального GGUF, не объявляя кэш готовым
    private static List<CacheEntry>? ReadMarker(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<CacheEntry>>(File.ReadAllText(path)) : null; }
        catch (JsonException ex) { Trace.TraceWarning("Маркер кэша RPC: {0}", ex.GetType().Name); return null; }
    }

    // Проверяет наличие и метаданные каждого ранее подготовленного файла
    private bool IsCurrent(CacheEntry entry)
    {
        if (entry.Name.Length != 16 || !entry.Name.All(Uri.IsHexDigit)) return false;
        var file = new FileInfo(Path.Combine(CacheRoot, "rpc", entry.Name));
        return file.Exists && file.Length == entry.Length && file.LastWriteTimeUtc.Ticks == entry.WriteTicks;
    }

    // Возвращает только отпечатки и объём подготовленного кэша, без локальных путей
    private static LocalModelStatus Status(LocalModelIdentity identity, List<CacheEntry> entries)
        => new() { Configured = true, Identity = identity, CachedTensors = entries.Count, CachedBytes = entries.Sum(entry => entry.Length) };

    private sealed record CacheEntry(string Name, long Length, long WriteTicks);
}
