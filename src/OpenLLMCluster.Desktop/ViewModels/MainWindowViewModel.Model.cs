namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public string[] EffortChoices => _modelCapabilities.SupportsEffort == true
        ? _modelCapabilities.EffortLevels.Where(value => value != "default").Distinct(StringComparer.Ordinal).ToArray() : [];
    public string EffortValuesText => EffortChoices.Length > 0 ? string.Join(", ", EffortChoices) : Text["EffortNoLevels"];
    public bool HasEffortLevels => EffortChoices.Length > 0;
    private string _effortNoticeKey = "EffortClientDefault";
    public string ModelEffortNotice => Text[_effortNoticeKey];
    public string[] ReasoningChoices { get; } = ["auto", "on", "off"];
    public string[] CacheChoices => ModelSettings.CacheTypes;
    [ObservableProperty] private int _maxOutputTokens = 4096;
    [ObservableProperty] private double _temperature = 0.8;
    [ObservableProperty] private int _topK = 40;
    [ObservableProperty] private double _topP = 0.95;
    [ObservableProperty] private double _minP = 0.05;
    [ObservableProperty] private double _repeatPenalty = 1;
    [ObservableProperty] private string _reasoningMode = "auto";
    [ObservableProperty] private string _reasoningEffort = "default";
    [ObservableProperty] private int _reasoningBudget = -1;
    [ObservableProperty] private int _parallelRequests = 1;
    [ObservableProperty] private int _batchSize = 512;
    [ObservableProperty] private int _microBatchSize = 128;
    [ObservableProperty] private bool _flashAttention = true;
    [ObservableProperty] private string _cacheTypeK = "f16";
    [ObservableProperty] private string _cacheTypeV = "f16";
    [ObservableProperty] private string _devices = "";
    [ObservableProperty] private string _tensorSplit = "";
    [ObservableProperty] private int _deviceReserveMiB = 1024;
    private ModelCapabilities _modelCapabilities = new(null, []);
    public bool CanSelectEffort => false;
    public string EffortSupport => Text[_modelCapabilities.SupportsEffort switch
        { true => "EffortSupported", false => "EffortUnsupported", _ => "EffortUnknown" }]
        + (_modelCapabilities.EffortLevels.Length > 0 ? " · " + string.Join(", ", _modelCapabilities.EffortLevels) : "");

    // Обновляет поддержку после фактической загрузки, не делая выводов по имени GGUF
    private void ApplyCapabilities(ModelCapabilities capabilities)
    {
        _modelCapabilities = capabilities;
        OnPropertyChanged(nameof(EffortChoices));
        OnPropertyChanged(nameof(EffortValuesText));
        OnPropertyChanged(nameof(HasEffortLevels));
        ReasoningEffort = "default";
        OnPropertyChanged(nameof(CanSelectEffort));
        OnPropertyChanged(nameof(EffortSupport));
    }

    // Восстанавливает новые параметры; старые профили получают значения по умолчанию
    private void ApplyModelSettings(ModelSettings settings)
    {
        MaxOutputTokens = settings.MaxOutputTokens;
        Temperature = settings.Temperature;
        TopK = settings.TopK;
        TopP = settings.TopP;
        MinP = settings.MinP;
        RepeatPenalty = settings.RepeatPenalty;
        ReasoningMode = settings.ReasoningMode;
        ReasoningEffort = "default";
        ReasoningBudget = settings.ReasoningBudget;
        ParallelRequests = settings.Parallel;
        BatchSize = settings.BatchSize;
        MicroBatchSize = settings.MicroBatchSize;
        FlashAttention = settings.FlashAttention;
        CacheTypeK = settings.CacheTypeK;
        CacheTypeV = settings.CacheTypeV;
        Devices = settings.Devices;
        TensorSplit = settings.TensorSplit;
        DeviceReserveMiB = settings.DeviceReserveMiB;
    }

    // Формирует значения, которые действительно попадут в аргументы следующего запуска
    private ModelSettings CreateModelSettings() => new()
    {
        MaxOutputTokens = MaxOutputTokens, Temperature = Temperature, TopK = TopK, TopP = TopP, MinP = MinP, RepeatPenalty = RepeatPenalty,
        ReasoningMode = ReasoningMode, ReasoningEffort = "default", ReasoningBudget = ReasoningBudget, Parallel = ParallelRequests,
        BatchSize = BatchSize, MicroBatchSize = MicroBatchSize, FlashAttention = FlashAttention, CacheTypeK = CacheTypeK, CacheTypeV = CacheTypeV,
        Devices = Devices.Trim(), TensorSplit = TensorSplit.Trim(), DeviceReserveMiB = DeviceReserveMiB
    };

    // Копирует только уровни, подтверждённые загруженной моделью
    [RelayCommand]
    private async Task CopyEffortAsync(IClipboard? clipboard)
    {
        if (clipboard is null || EffortChoices.Length == 0) return;
        try { await clipboard.SetTextAsync(string.Join(", ", EffortChoices)); }
        catch (Exception ex) { ReportError(ex); }
    }

    // Получает служебные маркеры шлюза, не читая текст запросов клиента
    private void ObserveEffort(string line)
    {
        var key = line switch
        {
            "OLC_EFFORT_FALLBACK" => "EffortClientFallback", "OLC_EFFORT_CLIENT" => "EffortFromClient",
            "OLC_EFFORT_DEFAULT" => "EffortClientDefault", _ => null
        };
        if (key is null) return;
        _effortNoticeKey = key;
        OnPropertyChanged(nameof(ModelEffortNotice));
    }
}
