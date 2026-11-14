namespace OpenLLMCluster.Desktop.ViewModels;

/// <summary>Карточка конкретного компьютера с отметкой актуальности, без фиктивной нулевой нагрузки</summary>
public partial class ComputerLoadViewModel(Localizer text) : ObservableObject
{
    public Localizer Text { get; } = text;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _hostName = "";
    [ObservableProperty] private string _statusKey = "MonitorWaiting";
    [ObservableProperty] private string _errorDetail = "";
    [ObservableProperty] private double _cpuUsage;
    [ObservableProperty] private bool _hasCpu;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private DateTimeOffset? _updatedAt;
    public ObservableCollection<GpuLoadViewModel> Gpus { get; } = [];
    public string Status => Text[StatusKey];
    public string CpuLabel => IsOnline && HasCpu ? $"{CpuUsage:F0}%" : Text["MeasurementUnavailable"];
    public string UpdatedLabel => UpdatedAt is { } value ? Text["LastSample"] + " " + value.ToLocalTime().ToString("HH:mm:ss") : "";
    public bool HasNoGpus => Gpus.Count == 0;
    public long? MemoryTotalBytes { get; private set; }
    public long? MemoryAvailableBytes { get; private set; }
    public bool HasMemory => IsOnline && MemoryTotalBytes is > 0 && MemoryAvailableBytes is >= 0 && MemoryAvailableBytes <= MemoryTotalBytes;
    public double MemoryUsage => HasMemory ? 100d * (1 - (double)MemoryAvailableBytes!.Value / MemoryTotalBytes!.Value) : 0;
    public string MemoryLabel => HasMemory
        ? $"{(MemoryTotalBytes!.Value - MemoryAvailableBytes!.Value) / 1073741824d:F1} / {MemoryTotalBytes.Value / 1073741824d:F1} GiB · {MemoryUsage:F0}%"
        : Text["MeasurementUnavailable"];

    /// <summary>Применяет фактический снимок без смешения показателей разных компьютеров</summary>
    public void Apply(NodeSnapshot snapshot)
    {
        IsOnline = true;
        HostName = snapshot.HostName;
        StatusKey = snapshot.TelemetryAvailable ? "MonitorOnline" : "MonitorUpdateAgent";
        ErrorDetail = "";
        HasCpu = snapshot.CpuUsagePercent.HasValue;
        CpuUsage = snapshot.CpuUsagePercent ?? 0;
        MemoryTotalBytes = snapshot.MemoryTotalBytes;
        MemoryAvailableBytes = snapshot.MemoryAvailableBytes;
        UpdatedAt = snapshot.SampledAt == default ? DateTimeOffset.UtcNow : snapshot.SampledAt;
        Gpus.Clear();
        foreach (var gpu in snapshot.Gpus) Gpus.Add(new(gpu, Text));
        RefreshLabels();
    }

    /// <summary>Сохраняет последнюю отметку времени, но скрывает старые числа при потере связи</summary>
    public void Unavailable(string key, string detail = "")
    {
        IsOnline = false;
        HasCpu = false;
        MemoryTotalBytes = null;
        MemoryAvailableBytes = null;
        StatusKey = key;
        ErrorDetail = detail;
        RefreshLabels();
    }

    /// <summary>Обновляет локализованные подписи без изменения измерений</summary>
    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(CpuLabel));
        OnPropertyChanged(nameof(UpdatedLabel));
        OnPropertyChanged(nameof(HasNoGpus));
        OnPropertyChanged(nameof(HasMemory));
        OnPropertyChanged(nameof(MemoryUsage));
        OnPropertyChanged(nameof(MemoryLabel));
        foreach (var gpu in Gpus) gpu.RefreshLabels();
    }
}

/// <summary>Нагрузка одного GPU и доля его физической видеопамяти</summary>
public sealed class GpuLoadViewModel(GpuSnapshot snapshot, Localizer text) : ObservableObject
{
    public string Name => $"GPU {snapshot.Index} · {snapshot.Name}";
    public bool HasLoad => snapshot.UtilizationPercent.HasValue;
    public double Load => snapshot.UtilizationPercent ?? 0;
    public string LoadLabel => HasLoad ? $"{Load:F0}%" : text["MeasurementUnavailable"];
    public bool HasMemory => snapshot.MemoryTotalMiB > 0 && snapshot.MemoryUsedMiB >= 0;
    public double MemoryPercent => HasMemory ? Math.Clamp(100d * snapshot.MemoryUsedMiB / snapshot.MemoryTotalMiB, 0, 100) : 0;
    public string MemoryLabel => HasMemory
        ? $"{snapshot.MemoryUsedMiB / 1024d:F1} / {snapshot.MemoryTotalMiB / 1024d:F1} GiB VRAM" : text["MeasurementUnavailable"];

    /// <summary>Обновляет текст отсутствующего показателя при смене языка</summary>
    public void RefreshLabels() { OnPropertyChanged(nameof(LoadLabel)); OnPropertyChanged(nameof(MemoryLabel)); }
}
