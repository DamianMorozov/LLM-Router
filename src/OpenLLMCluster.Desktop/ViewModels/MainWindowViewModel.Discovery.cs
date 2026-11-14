namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private readonly LocalInstallationDiscovery _installationDiscovery;
    public ObservableCollection<RuntimeLocation> RuntimeLocations { get; } = [];
    public ObservableCollection<string> ModelDirectories { get; } = [];
    public bool CanFindRuntime => CanEditEngine && !IsDiscovering;
    public bool CanFindModels => CanEditCoordinatorSettings && !IsDiscovering;
    public bool HasRuntimeLocations => RuntimeLocations.Count > 0;
    public bool HasModelDirectories => ModelDirectories.Count > 0;
    [ObservableProperty] private RuntimeLocation? _selectedRuntimeLocation;
    [ObservableProperty] private string? _selectedModelDirectory;
    [ObservableProperty] private string _modelDirectory = "";
    private string _runtimeDiscoveryKey = "";
    private string _modelDiscoveryKey = "";
    private bool _runtimeDiscoveryReadErrors;
    private bool _modelDiscoveryReadErrors;
    public string RuntimeDiscoveryStatus => FormatDiscoveryStatus(_runtimeDiscoveryKey, _runtimeDiscoveryReadErrors);
    public string ModelDiscoveryStatus => FormatDiscoveryStatus(_modelDiscoveryKey, _modelDiscoveryReadErrors);
    [ObservableProperty] private bool _isDiscovering;

    // Применяет явно выбранный пользователем результат поиска
    partial void OnSelectedRuntimeLocationChanged(RuntimeLocation? value)
    {
        if (value is not null) RuntimeDirectory = value.Directory;
    }

    // Не сбрасывает вручную введённый путь при очистке списка результатов
    partial void OnSelectedModelDirectoryChanged(string? value)
    {
        if (value is not null) ModelDirectory = value;
    }

    // Ищет каталоги и выбирает первый с нужным исполняемым файлом для текущей роли
    [RelayCommand(CanExecute = nameof(CanFindRuntime))]
    private async Task FindRuntimeAsync(CancellationToken ct)
    {
        IsDiscovering = true;
        _runtimeDiscoveryReadErrors = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
            var found = await _installationDiscovery.FindAsync(RuntimeDirectory, ModelDirectory, linked.Token);
            SelectedRuntimeLocation = null;
            RuntimeLocations.Clear();
            foreach (var location in found.Runtimes) RuntimeLocations.Add(location);
            OnPropertyChanged(nameof(HasRuntimeLocations));
            SelectedRuntimeLocation = found.Runtimes.FirstOrDefault(location => RoleIndex == (int)NodeRole.Worker ? location.HasRpc : location.HasServer);
            _runtimeDiscoveryKey = SelectedRuntimeLocation is not null ? "RuntimeDiscovered"
                : found.Runtimes.Count > 0 ? "RuntimeRoleMissing" : "RuntimeNotDiscovered";
            _runtimeDiscoveryReadErrors = found.HasReadErrors;
            Events.Add(RuntimeDiscoveryStatus);
        }
        catch (OperationCanceledException) { _runtimeDiscoveryKey = "AgentActionCancelled"; }
        catch (Exception ex) { _runtimeDiscoveryKey = "OperationFailed"; ReportError(ex); }
        finally { OnPropertyChanged(nameof(RuntimeDiscoveryStatus)); IsDiscovering = false; }
    }

    // Предлагает пользовательский каталог загрузок LM Studio, затем существующие стандартные каталоги
    [RelayCommand(CanExecute = nameof(CanFindModels))]
    private async Task FindModelsAsync(CancellationToken ct)
    {
        IsDiscovering = true;
        _modelDiscoveryReadErrors = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
            var found = await _installationDiscovery.FindAsync(RuntimeDirectory, ModelDirectory, linked.Token);
            SelectedModelDirectory = null;
            ModelDirectories.Clear();
            foreach (var directory in found.ModelDirectories) ModelDirectories.Add(directory);
            OnPropertyChanged(nameof(HasModelDirectories));
            SelectedModelDirectory = ModelDirectories.FirstOrDefault();
            _modelDiscoveryKey = ModelDirectories.Count > 0 ? "ModelsDiscovered" : "ModelsNotDiscovered";
            _modelDiscoveryReadErrors = found.HasReadErrors;
            Events.Add(ModelDiscoveryStatus);
        }
        catch (OperationCanceledException) { _modelDiscoveryKey = "AgentActionCancelled"; }
        catch (Exception ex) { _modelDiscoveryKey = "OperationFailed"; ReportError(ex); }
        finally { OnPropertyChanged(nameof(ModelDiscoveryStatus)); IsDiscovering = false; }
    }

    // Переводит текущее состояние поиска заново при смене языка
    private string FormatDiscoveryStatus(string key, bool readErrors)
        => key.Length == 0 ? "" : Text[key] + (readErrors ? " · " + Text["DiscoveryReadErrors"] : "");

    // Очищает только пользовательский журнал, не меняя результаты проверок и состояние агента
    [RelayCommand]
    private void ClearJournal() { Events.Clear(); EngineEvents.Clear(); Status = Text["JournalCleared"]; }

    // Копирует журнал в буфер текущего окна; подтверждение не добавляется в сам копируемый журнал
    [RelayCommand]
    private async Task CopyJournalAsync(IClipboard? clipboard)
    {
        if (clipboard is null) { Status = Text["ClipboardUnavailable"]; return; }
        try { await clipboard.SetTextAsync(GetJournalText()); Status = Text["JournalCopied"]; }
        catch (Exception ex) { ReportError(ex); }
    }

    /// <summary>Возвращает снимок журнала для копирования, исключая известные ключи текущего сеанса</summary>
    public string GetJournalText() => RedactJournal(Events.Concat(EngineEvents));

    // Очищает вывод движка, сохраняя общий журнал, результаты проверок и состояние процессов
    [RelayCommand]
    private void ClearEngineLog() { EngineEvents.Clear(); Status = Text["EngineLogCleared"]; }

    // Копирует только вывод llama.cpp с той же защитой ключей, что и общий журнал
    [RelayCommand]
    private async Task CopyEngineLogAsync(IClipboard? clipboard)
    {
        if (clipboard is null) { Status = Text["ClipboardUnavailable"]; return; }
        try { await clipboard.SetTextAsync(RedactJournal(EngineEvents)); Status = Text["EngineLogCopied"]; }
        catch (Exception ex) { ReportError(ex); }
    }

    // Скрывает известные ключи в снимке выбранного журнала
    private string RedactJournal(IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        foreach (var token in Nodes.Select(node => node.AccessToken).Append(LocalAgentToken).Append(ServerApiKey).Where(token => !string.IsNullOrEmpty(token)))
            text = text.Replace(token, "[token]", StringComparison.Ordinal);
        return text;
    }
}
