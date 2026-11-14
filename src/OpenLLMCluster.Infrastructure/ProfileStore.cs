namespace OpenLLMCluster.Infrastructure;

/// <summary>Сохраняет настройки в пользовательском каталоге приложения; секреты в модель не входят</summary>
public sealed class ProfileStore(string? filePath = null)
{
    private static readonly JsonSerializerOptions _options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public string FilePath { get; } = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Open-LLM-Cluster", "profile.json");
    public string StartupFilePath => FilePath + ".startup.json";
    public ProtectedSecretStore Secrets => new(FilePath + ".secrets");

    /// <summary>Читает только флаг автоматического сохранения и загрузки, не обращаясь к профилю</summary>
    public bool LoadAutomaticSettings() => Read<StartupSettings>(StartupFilePath).RememberSettings;

    /// <summary>Запоминает переключатель отдельно, в том числе когда сохранение профиля выключено</summary>
    public void SaveAutomaticSettings(bool enabled) => Write(StartupFilePath, new StartupSettings { RememberSettings = enabled });

    /// <summary>Читает небольшой профиль перед показом окна</summary>
    public RouterProfile Load() => Validate(Read<RouterProfile>(FilePath));

    /// <summary>Завершает запись небольшого профиля до штатного закрытия окна</summary>
    public void Save(RouterProfile profile) => Write(FilePath, profile);

    /// <summary>Читает профиль или возвращает исходные настройки при первом запуске</summary>
    public async Task<RouterProfile> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            await using var stream = File.OpenRead(FilePath);
            var profile = await JsonSerializer.DeserializeAsync<RouterProfile>(stream, _options, ct)
                ?? throw new InvalidDataException("Файл профиля не содержит настроек");
            return Validate(profile);
        }
        catch (Exception ex) { Trace.TraceError("Load profile: {0}", ex); throw; }
    }

    /// <summary>Записывает профиль без ключей доступа</summary>
    public async Task SaveAsync(RouterProfile profile, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            await using var stream = File.Create(FilePath);
            await JsonSerializer.SerializeAsync(stream, profile, _options, ct);
        }
        catch (Exception ex) { Trace.TraceError("Save profile: {0}", ex); throw; }
    }

    // Проверяет совместимость сохранённого профиля
    private static RouterProfile Validate(RouterProfile profile)
    {
        if (profile.SchemaVersion != 1 || profile.Nodes is null || profile.Window is null || profile.Agent is null || profile.ModelSettings is null)
            throw new InvalidDataException("Неподдерживаемый формат профиля");
        return profile;
    }

    // Читает настройки синхронно на границе жизненного цикла без гонки с закрытием приложения
    private static T Read<T>(string path) where T : new()
    {
        try
        {
            if (!File.Exists(path)) return new();
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, _options) ?? throw new InvalidDataException("Файл не содержит настроек");
        }
        catch (Exception ex) { Trace.TraceError("Read settings: {0}", ex); throw; }
    }

    // Записывает небольшой файл до возврата управления закрывающемуся окну
    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = File.Create(path);
            JsonSerializer.Serialize(stream, value, _options);
        }
        catch (Exception ex) { Trace.TraceError("Write settings: {0}", ex); throw; }
    }

    // Единственная настройка, которую нужно читать при отключённой загрузке профиля
    private sealed record StartupSettings
    {
        public bool RememberSettings { get; init; } = true;
    }
}
