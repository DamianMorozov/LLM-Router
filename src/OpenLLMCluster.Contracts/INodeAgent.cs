namespace OpenLLMCluster.Contracts;

/// <summary>Диагностический контракт, отдельный от разрешённых операций INodeControl</summary>
public interface INodeAgent : IService<INodeAgent>
{
    /// <summary>Возвращает актуальное состояние компьютера с агентом</summary>
    UnaryResult<NodeSnapshot> GetStatusAsync();
}

/// <summary>Снимок окружения, полученный на компьютере с агентом</summary>
[MessagePackObject]
public sealed class NodeSnapshot
{
    [Key(0)] public string HostName { get; set; } = "";
    [Key(1)] public string OperatingSystem { get; set; } = "";
    [Key(2)] public int LogicalProcessors { get; set; }
    [Key(3)] public long ManagedMemoryBudgetBytes { get; set; }
    [Key(4)] public string GpuInformation { get; set; } = "";
    [Key(5)] public bool NvidiaQuerySucceeded { get; set; }
    [Key(6)] public string AgentVersion { get; set; } = "";
    [Key(7)] public int ProtocolVersion { get; set; } = 1;
    [Key(8)] public string LlamaReleaseTag { get; set; } = "";
    [Key(9)] public string LlamaBackend { get; set; } = "";
    [Key(10)] public bool LlamaRpcAvailable { get; set; }
    [Key(11)] public double? CpuUsagePercent { get; set; }
    [Key(12)] public List<GpuSnapshot> Gpus { get; set; } = [];
    [Key(13)] public DateTimeOffset SampledAt { get; set; }
    [Key(14)] public bool TelemetryAvailable { get; set; }
    [Key(15)] public long? MemoryTotalBytes { get; set; }
    [Key(16)] public long? MemoryAvailableBytes { get; set; }
    [Key(17)] public bool LocalModelConfigured { get; set; }
}

/// <summary>Нагрузка и память одного физического NVIDIA GPU; отсутствие измерения не подменяется нулём</summary>
[MessagePackObject]
public sealed class GpuSnapshot
{
    [Key(0)] public int Index { get; set; }
    [Key(1)] public string Name { get; set; } = "";
    [Key(2)] public double? UtilizationPercent { get; set; }
    [Key(3)] public long MemoryTotalMiB { get; set; }
    [Key(4)] public long MemoryUsedMiB { get; set; }
}
