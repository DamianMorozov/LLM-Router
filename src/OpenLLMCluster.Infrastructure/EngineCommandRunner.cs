namespace OpenLLMCluster.Infrastructure;

/// <summary>Выполняет короткие проверки движка без оболочки и завершает свой процесс по таймауту</summary>
public sealed class EngineCommandRunner(Func<LaunchPlan, ProcessStartInfo>? startFactory = null)
{
    public Func<LaunchPlan, ProcessStartInfo> StartFactory { get; } = startFactory ?? CreateStartInfo;

    /// <summary>Проверяет CLI с ограничением времени и возвращает stdout и stderr</summary>
    public async Task<string> RunAsync(LaunchPlan plan, CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = StartFactory(plan) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output + Environment.NewLine + await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Проверка движка завершилась с кодом " + process.ExitCode + ": " + text);
            return text;
        }
        catch (Exception ex) { Trace.TraceError("Проверка движка: {0}", ex.GetType().Name); throw; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>Подтверждает наличие server/RPC и параметра --rpc, не загружая модель</summary>
    public async Task<string> ValidateBuildAsync(string directory, CancellationToken ct = default)
    {
        foreach (var role in new[] { NodeRole.Coordinator, NodeRole.Worker })
            if (!File.Exists(Path.Combine(directory, RuntimePlanner.GetExecutableName(role))))
                throw new FileNotFoundException("В сборке отсутствует " + RuntimePlanner.GetExecutableName(role));
        var server = Path.Combine(directory, RuntimePlanner.GetExecutableName(NodeRole.Coordinator));
        var help = await RunAsync(new(server, ["--help"]), ct);
        if (!help.Contains("--rpc", StringComparison.Ordinal)) throw new InvalidDataException("llama-server собран без поддержки --rpc");
        var rpcHelp = await RunAsync(new(Path.Combine(directory, RuntimePlanner.GetExecutableName(NodeRole.Worker)), ["--help"]), ct);
        if (!rpcHelp.Contains("--host", StringComparison.Ordinal)) throw new InvalidDataException("Не распознан CLI вычислительного RPC");
        return (await RunAsync(new(server, ["--version"]), ct)).Trim();
    }

    /// <summary>Создаёт отдельный процесс с передачей аргументов без интерпретации оболочкой</summary>
    public static ProcessStartInfo CreateStartInfo(LaunchPlan plan)
    {
        var start = new ProcessStartInfo(plan.Executable) { WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(plan.Executable))!,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in plan.Arguments) start.ArgumentList.Add(argument);
        return start;
    }
}
