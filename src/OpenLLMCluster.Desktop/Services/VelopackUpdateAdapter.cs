namespace OpenLLMCluster.Desktop.Services;

/// <summary>Использует публичные GitHub Releases и канал установленного пакета для текущей платформы</summary>
public sealed class VelopackUpdateAdapter : IApplicationUpdateAdapter
{
    private readonly IVelopackLocator _locator;
    private readonly Func<CancellationToken, IFileDownloader> _createDownloader;
    private UpdateInfo? _availableUpdate;
    public bool IsInstalled { get; }
    public string CurrentVersion { get; }
    public string Channel { get; }

    /// <summary>Определяет установку и проверяет канал, не обращаясь к сети</summary>
    public VelopackUpdateAdapter(IVelopackLocator? locator = null, string? runtimeIdentifier = null,
        Func<CancellationToken, IFileDownloader>? createDownloader = null)
    {
        _locator = locator ?? VelopackLocator.Current;
        _createDownloader = createDownloader ?? (ct => new UpdateFeedDownloader(ct));
        IsInstalled = _locator.CurrentlyInstalledVersion is not null;
        CurrentVersion = _locator.CurrentlyInstalledVersion?.ToFullString() ?? ApplicationInfo.Version;
        var rid = runtimeIdentifier ?? RuntimeInformation.RuntimeIdentifier;
        if (rid is not ("win-x64" or "linux-x64" or "osx-arm64" or "osx-x64"))
            throw new NotSupportedException("Для этой платформы канал обновления пока не предусмотрен");
        Channel = IsInstalled ? _locator.Channel ?? "" : rid + "-stable";
        if (IsInstalled && (_locator.AppId != ApplicationInfo.PackageId || (Channel != rid + "-stable" && Channel != rid + "-beta")))
            throw new InvalidDataException("Идентификатор или канал установленного пакета не соответствует приложению и платформе");
    }

    /// <summary>Запрашивает метаданные своего канала; пакеты не скачивает и установку не изменяет</summary>
    public async Task<AvailableApplicationUpdate?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            _availableUpdate = null;
            ct.ThrowIfCancellationRequested();
            if (!IsInstalled) return null;
            var prerelease = Channel.EndsWith("-beta", StringComparison.Ordinal);
            var manager = CreateManager(ct);
            var update = await manager.CheckForUpdatesAsync();
            ct.ThrowIfCancellationRequested();
            if (update is null) return null;
            var release = update.TargetFullRelease;
            if (update.IsDowngrade || release.PackageId != ApplicationInfo.PackageId || (!prerelease && release.Version.IsPrerelease))
                throw new InvalidDataException("Источник вернул пакет другого приложения или неподходящего канала");
            if (string.IsNullOrWhiteSpace(release.FileName) || Path.GetFileName(release.FileName) != release.FileName
                || release.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !release.FileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Источник вернул некорректное имя пакета");
            _availableUpdate = new UpdateInfo(release, isDowngrade: false);
            return new(release.Version.ToFullString(), release.NotesMarkdown ?? "");
        }
        catch (Exception ex) { Trace.TraceError("Velopack feed check: {0}", ex.GetType().Name); throw; }
    }

    /// <summary>Загружает full-пакет выбранной версии и проверяет размер и хеш, включая ранее скачанную копию</summary>
    public async Task DownloadAsync(Action<int> progress, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var update = _availableUpdate ?? throw new InvalidOperationException("Сначала проверьте наличие обновления");
            var package = Path.Combine(_locator.PackagesDir ?? throw new InvalidOperationException("Не определён каталог пакетов"),
                update.TargetFullRelease.FileName);
            // Velopack пропускает проверку готового файла, поэтому повреждённую локальную копию удаляем перед повтором
            if (File.Exists(package) && !await VerifyPackageAsync(package, update.TargetFullRelease, ct)) File.Delete(package);
            await CreateManager(ct).DownloadUpdatesAsync(update, progress, ct);
            ct.ThrowIfCancellationRequested();
            if (!await VerifyPackageAsync(package, update.TargetFullRelease, ct))
            {
                File.Delete(package);
                throw new InvalidDataException("Размер или контрольная сумма пакета обновления не совпадает");
            }
        }
        catch (Exception ex) { Trace.TraceError("Velopack package download: {0}", ex); throw; }
    }

    /// <summary>Проверяет неизменность скачанного пакета и наличие обновлятора без запуска установки</summary>
    public async Task ValidateReadyAsync(CancellationToken ct = default)
    {
        try
        {
            var update = _availableUpdate ?? throw new InvalidOperationException("Не выбрана версия обновления");
            var package = Path.Combine(_locator.PackagesDir ?? throw new InvalidOperationException("Не определён каталог пакетов"),
                update.TargetFullRelease.FileName);
            if (!await VerifyPackageAsync(package, update.TargetFullRelease, ct)) throw new InvalidDataException("Пакет обновления повреждён");
            if (!File.Exists(_locator.UpdateExePath)) throw new FileNotFoundException("Не найден исполняемый файл обновлятора");
        }
        catch (Exception ex) { Trace.TraceError("Velopack apply validation: {0}", ex); throw; }
    }

    /// <summary>Запускает штатное применение и перезапуск после завершённой подготовки приложения</summary>
    public void ApplyAndRestart()
    {
        try
        {
            var update = _availableUpdate ?? throw new InvalidOperationException("Не выбрана версия обновления");
            CreateManager(CancellationToken.None).ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
        catch (Exception ex) { Trace.TraceError("Velopack apply: {0}", ex); throw; }
    }

    // Создаёт источник с токеном текущей операции, не сохраняя отменённый токен предыдущей проверки
    private UpdateManager CreateManager(CancellationToken ct)
    {
        var source = new GithubSource(ApplicationInfo.RepositoryUrl, accessToken: null,
            prerelease: Channel.EndsWith("-beta", StringComparison.Ordinal), downloader: _createDownloader(ct));
        return new UpdateManager(source, new UpdateOptions { ExplicitChannel = Channel, AllowVersionDowngrade = false }, _locator);
    }

    // Не объявляет пакет готовым до завершения проверки содержимого на диске
    private static async Task<bool> VerifyPackageAsync(string path, VelopackAsset release, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length != release.Size) return false;
        var sha256 = !string.IsNullOrWhiteSpace(release.SHA256);
        var expected = sha256 ? release.SHA256 : release.SHA1;
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var hash = sha256 ? await SHA256.HashDataAsync(stream, ct) : await SHA1.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
