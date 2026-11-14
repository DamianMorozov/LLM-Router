namespace OpenLLMCluster.Infrastructure;

/// <summary>Собирает реальные данные окружения; отсутствие nvidia-smi не трактуется как отсутствие GPU</summary>
public sealed class EnvironmentProbe(LlamaInstallationStore? installationStore = null)
{
    private readonly CpuLoadProbe _cpu = new();
    /// <summary>Опрашивает окружение и NVIDIA с ограничением времени выполнения</summary>
    public async Task<NodeSnapshot> ReadAsync(CancellationToken ct = default)
    {
        var memoryTask = new MemoryLoadProbe().ReadAsync(ct);
        var snapshot = new NodeSnapshot
        {
            HostName = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription + " / " + RuntimeInformation.OSArchitecture,
            LogicalProcessors = Environment.ProcessorCount,
            ManagedMemoryBudgetBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            AgentVersion = typeof(EnvironmentProbe).Assembly.GetName().Version?.ToString() ?? "unknown",
            CpuUsagePercent = await _cpu.ReadAsync(ct), TelemetryAvailable = true, SampledAt = DateTimeOffset.UtcNow
        };
        var memory = await memoryTask;
        snapshot.MemoryTotalBytes = memory?.TotalBytes;
        snapshot.MemoryAvailableBytes = memory?.AvailableBytes;
        var installation = (installationStore ?? new LlamaInstallationStore()).ReadCurrent();
        if (installation is not null)
        {
            snapshot.LlamaReleaseTag = installation.ReleaseTag;
            snapshot.LlamaBackend = installation.Backend.ToString();
            snapshot.LlamaRpcAvailable = File.Exists(Path.Combine(installation.Directory, RuntimePlanner.GetExecutableName(NodeRole.Worker)));
        }
        var start = new ProcessStartInfo("nvidia-smi") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--query-gpu=index,name,memory.total,memory.used,utilization.gpu,driver_version");
        start.ArgumentList.Add("--format=csv,noheader");
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            var text = (await output).Trim();
            snapshot.NvidiaQuerySucceeded = process.ExitCode == 0 && text.Length > 0;
            snapshot.GpuInformation = snapshot.NvidiaQuerySucceeded ? text : (await error).Trim();
            if (snapshot.NvidiaQuerySucceeded) snapshot.Gpus = ParseNvidia(text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { KillOwnedProcess(process); throw; }
        catch (Exception ex)
        {
            KillOwnedProcess(process);
            Trace.TraceWarning("NVIDIA probe: {0}", ex.Message);
            snapshot.GpuInformation = "";
        }
        return snapshot;
    }

    /// <summary>Разбирает отдельные GPU; N/A не превращается в нулевую загрузку</summary>
    public static List<GpuSnapshot> ParseNvidia(string text)
    {
        var result = new List<GpuSnapshot>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Trim().Split(',').Select(value => value.Trim()).ToArray();
            if (fields.Length < 6 || !int.TryParse(fields[0], out var index)) continue;
            var total = ParseNumber(fields[^4]);
            var used = ParseNumber(fields[^3]);
            var load = ParseNumber(fields[^2]);
            result.Add(new() { Index = index, Name = string.Join(", ", fields[1..^4]), MemoryTotalMiB = (long)(total ?? 0),
                MemoryUsedMiB = (long)(used ?? -1), UtilizationPercent = load is >= 0 and <= 100 ? load : null });
        }
        return result;
    }

    // Единицы nvidia-smi удаляются, неизвестное значение сохраняется как отсутствие измерения
    private static double? ParseNumber(string text) => double.TryParse(text.Split(' ')[0], System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : null;

    // Завершает только запущенную этой проверкой утилиту
    private static void KillOwnedProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
