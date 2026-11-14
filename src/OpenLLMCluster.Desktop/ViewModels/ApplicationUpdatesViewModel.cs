namespace OpenLLMCluster.Desktop.ViewModels;

/// <summary>Представляет проверку, скачивание и явное подтверждение обновления без фоновой установки</summary>
public partial class ApplicationUpdatesViewModel : ObservableObject, IDisposable
{
    private readonly ApplicationUpdateService _service;
    private readonly Func<bool> _hasActiveWork;
    private readonly Func<CancellationToken, Task>? _install;
    private readonly Action<string>? _log;
    private CancellationTokenSource? _operation;
    private bool _disposed;
    private bool _installing;
    private bool _installFailed;
    public Localizer Text { get; }
    public string CurrentVersion => _service.Status.CurrentVersion;
    public string AvailableVersion => _service.Status.Release?.Version ?? "—";
    public string Channel => _service.Status.Channel;
    public string ReleaseNotes => _service.Status.Release?.NotesMarkdown is { Length: > 0 } notes ? notes : Text["UpdateNoNotes"];
    public string StatusText => Text[_installFailed ? "UpdateInstallError" : _installing ? "UpdateApplying" : _service.Status.MessageKey];
    public int Progress => _service.Status.Progress;
    public bool IsBusy => _operation is not null || _service.IsBusy;
    public bool IsChecking => _service.Status.State == ApplicationUpdateState.Checking;
    public bool IsDownloading => _service.Status.State == ApplicationUpdateState.Downloading;
    public bool HasRelease => _service.Status.Release is not null;
    public bool IsConfirmationVisible { get; private set; }
    public bool IsInstallationSupported => _install is not null;
    public string ConfirmationText => Text[_hasActiveWork() ? "UpdateConfirmBusy" : "UpdateConfirmIdle"];
    public bool CanCheck => !_disposed && !IsBusy && _service.Status.State != ApplicationUpdateState.NotInstalled;
    public bool CanDownload => !_disposed && !IsBusy && HasRelease
        && _service.Status.State is ApplicationUpdateState.Available or ApplicationUpdateState.Error;
    public bool CanRequestInstall => !_disposed && !IsBusy && _service.Status.State == ApplicationUpdateState.ReadyToInstall;
    public bool CanConfirmInstall => CanRequestInstall && IsConfirmationVisible && IsInstallationSupported;
    public bool CanCancel => !_disposed && _operation is not null && !_installing;
    public bool CanDismissConfirmation => IsConfirmationVisible && !_installing;

    /// <summary>Принимает единый сервис приложения и координатор безопасной установки</summary>
    public ApplicationUpdatesViewModel(ApplicationUpdateService service, Localizer text, Func<bool> hasActiveWork,
        Action<string>? log = null, Func<CancellationToken, Task>? install = null)
    {
        _service = service;
        Text = text;
        _hasActiveWork = hasActiveWork;
        _log = log;
        _install = install;
        _service.StatusChanged += OnStatusChanged;
        Text.PropertyChanged += OnLanguageChanged;
    }

    // Проверяет источник только по нажатию пользователя
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => RunAsync(_service.CheckAsync);

    // Скачивает пакет, оставляя модель и агент работающими
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadAsync() => RunAsync(_service.DownloadAsync);

    // Передаёт отмену текущему сетевому запросу
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _operation?.Cancel();

    // Открывает подтверждение без остановки процессов и применения пакета
    [RelayCommand(CanExecute = nameof(CanRequestInstall))]
    private void RequestInstall() { IsConfirmationVisible = true; Refresh(); }

    // Закрывает подтверждение, сохраняя скачанный пакет
    [RelayCommand(CanExecute = nameof(CanDismissConfirmation))]
    private void DismissConfirmation() { IsConfirmationVisible = false; Refresh(); }

    // Вызывает только подключённый координатор безопасной установки после отдельного подтверждения
    [RelayCommand(CanExecute = nameof(CanConfirmInstall))]
    private async Task ConfirmInstallAsync()
    {
        if (!CanConfirmInstall || _install is null) return;
        _operation = new CancellationTokenSource();
        _installing = true;
        _installFailed = false;
        Refresh();
        try { await _install(_operation.Token); }
        catch (Exception ex)
        {
            Trace.TraceError("Update installation: {0}", ex);
            _installFailed = true;
            if (!_disposed) _log?.Invoke($"{Text["UpdateInstallError"]} {ex.GetType().Name}: {ex.Message}");
        }
        finally { CompleteOperation(); }
    }

    // Объединяет отмену, повтор и завершение команд, не выпуская исключения в обработчик UI
    private async Task RunAsync(Func<CancellationToken, Task<bool>> action)
    {
        if (_disposed || IsBusy) return;
        _operation = new CancellationTokenSource();
        IsConfirmationVisible = false;
        _installFailed = false;
        Refresh();
        try { await action(_operation.Token); }
        catch (OperationCanceledException) when (_operation.IsCancellationRequested) { }
        finally
        {
            CompleteOperation();
            if (!_disposed) _log?.Invoke(StatusText);
        }
    }

    // Освобождает токен после фактического завершения операции
    private void CompleteOperation()
    {
        _operation?.Dispose();
        _operation = null;
        _installing = false;
        if (!_disposed) Refresh();
    }

    // Переносит события фонового скачивания на поток окна
    private void OnStatusChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    // Обновляет вычисляемые подписи при смене языка
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args) => Refresh();

    /// <summary>Обновляет предупреждение, если за время подтверждения изменилась работа модели или агента</summary>
    public void Refresh()
    {
        if (_disposed) return;
        OnPropertyChanged(string.Empty);
        CheckCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RequestInstallCommand.NotifyCanExecuteChanged();
        DismissConfirmationCommand.NotifyCanExecuteChanged();
        ConfirmInstallCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Отменяет незавершённую проверку или скачивание и отключает привязки закрытого окна</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.StatusChanged -= OnStatusChanged;
        Text.PropertyChanged -= OnLanguageChanged;
        _operation?.Cancel();
    }
}
