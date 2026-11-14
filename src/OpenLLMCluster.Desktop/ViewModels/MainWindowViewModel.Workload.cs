namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private bool _applyingWorkload;
    private WorkloadMode _appliedWorkload;
    [ObservableProperty] private int _workloadIndex;
    public LocalizedOption[] WorkloadOptions { get; }
    public string WorkloadSummary => Text[WorkloadIndex switch
        { 1 => "WorkloadInteractiveHelp", 2 => "WorkloadBalancedHelp", 3 => "WorkloadThroughputHelp", 4 => "WorkloadMaximumHelp", _ => "WorkloadCustomHelp" }];
    public bool CanApplyWorkload => CanEditEngine && IsCoordinatorRole && WorkloadIndex is >= 1 and <= 4;
    public string ModelId => InferenceDefaults.ModelId;

    // Применяет выбор явно и только к остановленному движку
    [RelayCommand(CanExecute = nameof(CanApplyWorkload))]
    private void ApplyWorkloadProfile()
    {
        if (!CanApplyWorkload) return;
        var profile = WorkloadProfiles.Apply(CreateProfile(), (WorkloadMode)WorkloadIndex, Environment.ProcessorCount);
        _applyingWorkload = true;
        try
        {
            ContextLength = profile.ContextLength;
            CpuThreads = profile.CpuThreads;
            ApplyModelSettings(profile.ModelSettings);
            _appliedWorkload = profile.Workload;
            CommandPreview = "";
            Events.Add(Text["WorkloadApplied"] + " · " + WorkloadOptions[WorkloadIndex].Label);
        }
        finally { _applyingWorkload = false; }
    }

    // Отмечает ручное изменение согласованных параметров как пользовательскую конфигурацию
    private void OnWorkloadSettingChanged(string? property)
    {
        if (property is nameof(WorkloadIndex) or nameof(CanEditEngine) or nameof(RoleIndex))
        { OnPropertyChanged(nameof(WorkloadSummary)); OnPropertyChanged(nameof(CanApplyWorkload)); ApplyWorkloadProfileCommand.NotifyCanExecuteChanged(); }
        if (_applyingWorkload || property is not (nameof(ContextLength) or nameof(CpuThreads) or nameof(ParallelRequests) or nameof(BatchSize)
            or nameof(MicroBatchSize) or nameof(FlashAttention) or nameof(CacheTypeK) or nameof(CacheTypeV) or nameof(DeviceReserveMiB) or nameof(MaxOutputTokens))) return;
        if (property == nameof(MaxOutputTokens) && _appliedWorkload != WorkloadMode.MaximumContext) return;
        _appliedWorkload = WorkloadMode.Custom;
        WorkloadIndex = 0;
    }

    // Копирует фактический серверный алиас независимо от имени GGUF
    [RelayCommand]
    private async Task CopyModelIdAsync(IClipboard? clipboard)
    {
        if (clipboard is null) { Status = Text["ClipboardUnavailable"]; return; }
        try { await clipboard.SetTextAsync(ModelId); Status = Text["ModelIdCopied"]; }
        catch (Exception ex) { ReportError(ex); }
    }
}
