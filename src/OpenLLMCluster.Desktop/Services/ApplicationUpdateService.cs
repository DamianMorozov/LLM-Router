namespace OpenLLMCluster.Desktop.Services;

/// <summary>Проверяет обновления только по запросу, сохраняя работоспособность приложения при ошибке источника</summary>
public sealed class ApplicationUpdateService
{
    private readonly Func<IApplicationUpdateAdapter> _createAdapter;
    private IApplicationUpdateAdapter? _adapter;
    private int _operation;
    private ApplicationUpdateStatus _status = new(ApplicationUpdateState.NotInstalled, ApplicationInfo.Version, "", "UpdateNotInstalled");
    public ApplicationUpdateStatus Status => Volatile.Read(ref _status);
    public string ReleasesUrl => ApplicationInfo.ReleasesUrl;
    public bool IsBusy => Status.State is ApplicationUpdateState.Checking or ApplicationUpdateState.Downloading or ApplicationUpdateState.Applying;
    public event EventHandler? StatusChanged;

    /// <summary>Определяет тип установки без обращения к GitHub и без запуска обновления</summary>
    public ApplicationUpdateService(Func<IApplicationUpdateAdapter>? createAdapter = null)
    {
        _createAdapter = createAdapter ?? (() => new VelopackUpdateAdapter());
        try { InitializeAdapter(); }
        catch (Exception ex) { ReportError(ex); }
    }

    /// <summary>Выполняет одну проверку; возвращает false, если другая операция уже выполняется</summary>
    public async Task<bool> CheckAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return false;
        var previous = Status;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_adapter is null) InitializeAdapter();
            if (!_adapter!.IsInstalled)
            {
                SetStatus(Status with { State = ApplicationUpdateState.NotInstalled, MessageKey = "UpdateNotInstalled", Release = null });
                return true;
            }
            SetStatus(Status with { State = ApplicationUpdateState.Checking, MessageKey = "UpdateChecking", Release = null, Progress = 0 });
            var release = await _adapter.CheckAsync(ct);
            ct.ThrowIfCancellationRequested();
            SetStatus(Status with { State = release is null ? ApplicationUpdateState.NoUpdate : ApplicationUpdateState.Available,
                MessageKey = release is null ? "UpdateNoUpdate" : "UpdateAvailable", Release = release });
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { SetStatus(previous); throw; }
        catch (Exception ex) { ReportError(ex); return true; }
        finally { Interlocked.Exchange(ref _operation, 0); }
    }

    /// <summary>Скачивает выбранную версию; отмена и ошибка допускают повторную попытку без новой проверки</summary>
    public async Task<bool> DownloadAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return false;
        var previous = Status;
        try
        {
            if (_adapter is null || previous.Release is null
                || previous.State is not (ApplicationUpdateState.Available or ApplicationUpdateState.Error)) return false;
            ct.ThrowIfCancellationRequested();
            SetStatus(previous with { State = ApplicationUpdateState.Downloading, MessageKey = "UpdateDownloading", Progress = 0 });
            await _adapter.DownloadAsync(ReportProgress, ct);
            ct.ThrowIfCancellationRequested();
            SetStatus(Status with { State = ApplicationUpdateState.ReadyToInstall, MessageKey = "UpdateReady", Progress = 100 });
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(previous with { State = ApplicationUpdateState.Available, MessageKey = "UpdateDownloadCancelled", Progress = 0 });
            throw;
        }
        catch (Exception ex) { ReportError(ex, keepRelease: true); return true; }
        finally { Interlocked.Exchange(ref _operation, 0); }
    }

    /// <summary>Применяет готовый пакет только после успешной остановки процессов и сохранения разрешённых настроек</summary>
    public async Task ApplyAsync(Func<CancellationToken, Task> prepare, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0) throw new InvalidOperationException("Другая операция обновления ещё выполняется");
        var previous = Status;
        try
        {
            if (_adapter is null || previous.State != ApplicationUpdateState.ReadyToInstall || previous.Release is null)
                throw new InvalidOperationException("Пакет ещё не готов к установке");
            SetStatus(previous with { State = ApplicationUpdateState.Applying, MessageKey = "UpdateApplying" });
            await _adapter.ValidateReadyAsync(ct);
            await prepare(ct);
            ct.ThrowIfCancellationRequested();
            _adapter.ApplyAndRestart();
        }
        catch (Exception ex)
        {
            Trace.TraceError("Safe update application: {0}", ex);
            SetStatus(previous with { MessageKey = "UpdateInstallError" });
            throw;
        }
        finally { Interlocked.Exchange(ref _operation, 0); }
    }

    // Прогресс 100% сам по себе ещё не означает успешную проверку пакета
    private void ReportProgress(int value)
    {
        if (Status.State == ApplicationUpdateState.Downloading) SetStatus(Status with { Progress = Math.Clamp(value, 0, 100) });
    }

    // Читает свойства установки до публикации адаптера, чтобы ошибка определения не оставила частичное состояние
    private void InitializeAdapter()
    {
        var adapter = _createAdapter();
        var status = new ApplicationUpdateStatus(adapter.IsInstalled ? ApplicationUpdateState.Idle : ApplicationUpdateState.NotInstalled,
            adapter.CurrentVersion, adapter.Channel, adapter.IsInstalled ? "UpdateIdle" : "UpdateNotInstalled");
        _adapter = adapter;
        SetStatus(status);
    }

    // Сохраняет причину сбоя в журнале без передачи необработанных сетевых сообщений в интерфейс
    private void ReportError(Exception ex, bool keepRelease = false)
    {
        Trace.TraceError("Application update failed: {0}", ex);
        var key = ex is HttpRequestException or TaskCanceledException ? "UpdateNetworkError" : "UpdateError";
        SetStatus(Status with { State = ApplicationUpdateState.Error, MessageKey = key, Release = keepRelease ? Status.Release : null, Progress = 0 });
    }

    // Передаёт наблюдателям целиком согласованный снимок состояния
    private void SetStatus(ApplicationUpdateStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
