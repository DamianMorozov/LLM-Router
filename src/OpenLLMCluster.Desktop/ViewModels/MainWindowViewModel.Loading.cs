namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private readonly Stopwatch _modelLoadClock = new();
    private readonly DispatcherTimer _modelLoadTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _modelLoadStageKey = "ModelNotLoaded";
    private bool _modelLoadTimerAttached;
    [ObservableProperty] private double _modelLoadProgressValue;
    public bool IsModelLoading => IsEngineStarting && IsCoordinatorRole;
    public string OverviewModelName => string.IsNullOrWhiteSpace(ModelPath) ? Text["NoModelSelected"] : Path.GetFileName(ModelPath);
    public string ModelLoadStageText => Text[_modelLoadStageKey];
    public string ModelLoadElapsedText => Text["ModelElapsed"] + $" {(int)_modelLoadClock.Elapsed.TotalMinutes}:{_modelLoadClock.Elapsed.Seconds:00}";

    // Запускает измерение времени реальной загрузки, без расчёта вымышленного процента по времени
    private void BeginModelLoading()
    {
        if (!IsCoordinatorRole) return;
        ModelLoadProgressValue = 0;
        SetModelLoadStage("ModelChecking");
        _modelLoadClock.Restart();
        if (!_modelLoadTimerAttached) { _modelLoadTimer.Tick += ModelLoadTimerTick; _modelLoadTimerAttached = true; }
        _modelLoadTimer.Start();
        OnPropertyChanged(nameof(ModelLoadElapsedText));
    }

    // Обновляет только подпись времени, независимо от интервала мониторинга компьютеров
    private void ModelLoadTimerTick(object? sender, EventArgs args) => OnPropertyChanged(nameof(ModelLoadElapsedText));

    // Сопоставляет известные строки llama.cpp с фактическим этапом; обработку запросов не принимает за загрузку
    private void ObserveModelLoading(string line)
    {
        if (!IsModelLoading) return;
        if (line.Contains("warming up", StringComparison.OrdinalIgnoreCase) || line.Contains("warmup", StringComparison.OrdinalIgnoreCase))
            SetModelLoadStage("ModelWarmingUp");
        else if (line.Contains("initializing, n_slots", StringComparison.Ordinal) || line.Contains("llama_context:", StringComparison.Ordinal))
            SetModelLoadStage("ModelCreatingContext");
        else if (line.Contains("loading model", StringComparison.OrdinalIgnoreCase) || line.Contains("load_tensors:", StringComparison.Ordinal))
            SetModelLoadStage("ModelLoadingWeights");
        else if (line.Contains("model loaded", StringComparison.OrdinalIgnoreCase)) SetModelLoadStage("ModelCheckingApi");
    }

    // Завершает измерение; значение 100 назначается только после подтверждённой готовности API
    private void CompleteModelLoading(string stage)
    {
        _modelLoadTimer.Stop();
        _modelLoadClock.Stop();
        ModelLoadProgressValue = stage == "ModelReady" ? 100 : 0;
        SetModelLoadStage(stage);
        OnPropertyChanged(nameof(ModelLoadElapsedText));
    }

    // Уведомляет локализованную подпись этапа
    private void SetModelLoadStage(string stage)
    {
        _modelLoadStageKey = stage;
        OnPropertyChanged(nameof(ModelLoadStageText));
    }

    /// <summary>Подтверждает завершение собственного процесса до закрытия окна и сохраняет окно открытым при ошибке</summary>
    public bool TryStopEngineForClose()
    {
        try
        {
            _engineOperation?.Cancel();
            _engine.Stop();
            IsEngineRunning = false;
            IsEngineStarting = false;
            CompleteModelLoading("ModelNotLoaded");
            return true;
        }
        catch (Exception ex) { ReportError(ex); return false; }
    }
}
