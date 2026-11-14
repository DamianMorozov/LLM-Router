namespace OpenLLMCluster.Core;

/// <summary>Параметры запуска и исходные значения генерации; клиент API может переопределить параметры запроса</summary>
public sealed record ModelSettings
{
    public int MaxOutputTokens { get; init; } = 4096;
    public double Temperature { get; init; } = 0.8;
    public int TopK { get; init; } = 40;
    public double TopP { get; init; } = 0.95;
    public double MinP { get; init; } = 0.05;
    public double RepeatPenalty { get; init; } = 1;
    public string ReasoningMode { get; init; } = "auto";
    public string ReasoningEffort { get; init; } = "default";
    public int ReasoningBudget { get; init; } = -1;
    public int Parallel { get; init; } = 1;
    public int BatchSize { get; init; } = 512;
    public int MicroBatchSize { get; init; } = 128;
    public bool FlashAttention { get; init; } = true;
    public string CacheTypeK { get; init; } = "f16";
    public string CacheTypeV { get; init; } = "f16";
    public string Devices { get; init; } = "";
    public string TensorSplit { get; init; } = "";
    public int DeviceReserveMiB { get; init; } = 1024;

    /// <summary>Проверяет диапазоны и поддерживаемые CLI значения перед построением аргументов</summary>
    public bool IsValid() => MaxOutputTokens > 0 && double.IsFinite(Temperature) && Temperature is >= 0 and <= 2
        && TopK >= 0 && double.IsFinite(TopP) && TopP is > 0 and <= 1 && double.IsFinite(MinP) && MinP is >= 0 and <= 1
        && double.IsFinite(RepeatPenalty) && RepeatPenalty is > 0 and <= 3 && ReasoningBudget >= -1
        && ReasoningMode is "auto" or "on" or "off"
        && Parallel is >= 1 and <= 16 && BatchSize > 0 && MicroBatchSize > 0 && MicroBatchSize <= BatchSize
        && CacheTypes.Contains(CacheTypeK) && CacheTypes.Contains(CacheTypeV) && (FlashAttention || CacheTypeV == "f16")
        && DeviceReserveMiB >= 0 && IsValidSplit();

    // Требует явного порядка устройств для пропорций, исключая неоднозначное применение к другому GPU
    private bool IsValidSplit()
    {
        var devices = Devices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (Devices.Length > 0 && (devices.Length == 0 || devices.Distinct(StringComparer.Ordinal).Count() != devices.Length
            || devices.Any(value => value.Length == 0 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.')))) return false;
        if (TensorSplit.Length == 0) return true;
        var weights = TensorSplit.Split(',', StringSplitOptions.TrimEntries);
        var sum = 0d;
        foreach (var text in weights)
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture,
                out var weight) || !double.IsFinite(weight) || weight < 0) return false;
            sum += weight;
        }
        return devices.Length == weights.Length && double.IsFinite(sum) && sum > 0;
    }

    public static string[] EffortLevels { get; } = ["default", "none", "minimal", "low", "medium", "high", "xhigh", "max"];
    public static string[] CacheTypes { get; } = ["f16", "q8_0", "q4_0"];
}
