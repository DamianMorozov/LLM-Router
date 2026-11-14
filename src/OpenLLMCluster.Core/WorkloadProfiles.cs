namespace OpenLLMCluster.Core;

/// <summary>Начальные варианты нагрузки; фактическая вместимость и скорость проверяются движком</summary>
public enum WorkloadMode { Custom, Interactive, Balanced, Throughput, MaximumContext }

/// <summary>Применяет согласованные параметры, сохраняя модель, устройства, сеть и настройки качества генерации</summary>
public static class WorkloadProfiles
{
    /// <summary>Готовит новый профиль для следующего запуска без изменения работающего движка</summary>
    public static RouterProfile Apply(RouterProfile source, WorkloadMode mode, int logicalProcessors)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode), "Неизвестный профиль нагрузки");
        if (mode == WorkloadMode.Custom) return source with { Workload = mode };
        var throughput = mode == WorkloadMode.Throughput;
        var maximum = mode == WorkloadMode.MaximumContext;
        return source with
        {
            Workload = mode, ContextLength = maximum ? 131072 : mode == WorkloadMode.Interactive ? 8192 : 16384,
            CpuThreads = Math.Clamp(logicalProcessors / 2, 1, 8),
            ModelSettings = source.ModelSettings with
            {
                MaxOutputTokens = maximum ? 32768 : source.ModelSettings.MaxOutputTokens,
                Parallel = throughput ? 2 : 1, BatchSize = throughput ? 1024 : 512, MicroBatchSize = throughput ? 256 : 128,
                DeviceReserveMiB = throughput ? 1536 : 1024
            }
        };
    }
}

/// <summary>Общее имя модели в CLI, диагностике и интерфейсе подключения клиента</summary>
public static class InferenceDefaults
{
    public const string ModelId = "open-llm-cluster";
}
