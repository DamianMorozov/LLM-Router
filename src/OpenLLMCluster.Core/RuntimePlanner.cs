namespace OpenLLMCluster.Core;

/// <summary>Проверяет локальные файлы и строит команды llama.cpp для выбранной роли</summary>
public sealed class RuntimePlanner
{
    /// <summary>Возвращает платформенное имя исполняемого файла</summary>
    public static string GetExecutableName(NodeRole role) => (role == NodeRole.Coordinator ? "llama-server" : "ggml-rpc-server")
        + (OperatingSystem.IsWindows() ? ".exe" : "");

    /// <summary>Проверяет профиль и сигнатуру GGUF без загрузки весов</summary>
    public async Task<IReadOnlyList<Diagnostic>> ValidateAsync(RouterProfile profile, CancellationToken ct = default)
    {
        var results = new List<Diagnostic>();
        if (!Enum.IsDefined(profile.Role)) results.Add(new("InvalidRole", true));
        if (!IPAddress.TryParse(profile.BindAddress, out var address)) results.Add(new("InvalidBindAddress", true));
        else if (!IPAddress.IsLoopback(address)) results.Add(new("LanBinding", false));
        if (!IsPort(profile.ApiPort) || !IsPort(profile.RpcPort)) results.Add(new("InvalidPort", true));
        if (profile.ContextLength < 512) results.Add(new("InvalidContext", true));
        if (profile.CpuThreads < 1) results.Add(new("InvalidThreads", true));
        if (profile.GpuLayers < -1 || profile.StartupTimeoutSeconds < 1) results.Add(new("InvalidEngineSettings", true));
        var executable = Path.Combine(profile.RuntimeDirectory, GetExecutableName(profile.Role));
        results.Add(new(File.Exists(executable) ? "RuntimeFound" : "RuntimeMissing", !File.Exists(executable), executable));
        if (profile.Role == NodeRole.Coordinator)
        {
            if (!profile.ModelSettings.IsValid()) results.Add(new("InvalidModelSettings", true));
            await ValidateModelAsync(profile.ModelPath, results, ct);
            foreach (var node in profile.Nodes.Where(n => n.Enabled))
                if (!IsValidNode(node)) results.Add(new("InvalidNode", true, node.Name));
            if (profile.Nodes.Where(n => n.Enabled).GroupBy(n => n.RpcEndpoint, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                results.Add(new("DuplicateNode", true));
        }
        results.Add(new("CompatibilityNotVerified", false));
        return results;
    }

    /// <summary>Формирует аргументы запуска; секреты в план не включаются</summary>
    public LaunchPlan Build(RouterProfile profile)
    {
        if (!Enum.IsDefined(profile.Role) || !IPAddress.TryParse(profile.BindAddress, out _)
            || !IsPort(profile.ApiPort) || !IsPort(profile.RpcPort) || profile.ContextLength < 512 || profile.CpuThreads < 1
            || profile.GpuLayers < -1 || profile.StartupTimeoutSeconds < 1
            || (profile.Role == NodeRole.Coordinator && (!profile.ModelSettings.IsValid() || profile.Nodes.Any(n => n.Enabled && !IsValidNode(n)))))
            throw new ArgumentException("Профиль содержит недопустимые параметры");
        var args = new List<string> { "--host", profile.BindAddress, "--port", (profile.Role == NodeRole.Coordinator ? profile.ApiPort : profile.RpcPort).ToString() };
        if (profile.Role == NodeRole.Worker) args.Add("--cache");
        else
        {
            var settings = profile.ModelSettings;
            args.AddRange(["--model", profile.ModelPath, "--alias", InferenceDefaults.ModelId, "--ctx-size", profile.ContextLength.ToString(),
                "--parallel", settings.Parallel.ToString(), "--n-gpu-layers", profile.GpuLayers == -1 ? "auto" : profile.GpuLayers.ToString(),
                "--split-mode", "layer",
                "--flash-attn", settings.FlashAttention ? "on" : "off", "--batch-size", settings.BatchSize.ToString(),
                "--ubatch-size", settings.MicroBatchSize.ToString(), "--threads", profile.CpuThreads.ToString(),
                "--predict", settings.MaxOutputTokens.ToString(), "--temp", Invariant(settings.Temperature), "--top-k", settings.TopK.ToString(),
                "--top-p", Invariant(settings.TopP), "--min-p", Invariant(settings.MinP), "--repeat-penalty", Invariant(settings.RepeatPenalty),
                "--reasoning", settings.ReasoningMode, "--reasoning-effort", "default", "--reasoning-budget", settings.ReasoningBudget.ToString(),
                "--cache-type-k", settings.CacheTypeK, "--cache-type-v", settings.CacheTypeV]);
            var nodes = profile.Nodes.Where(n => n.Enabled).Select(n => n.RpcEndpoint).ToArray();
            if (nodes.Length > 0) args.AddRange(["--rpc", string.Join(',', nodes)]);
            if (settings.Devices.Length > 0) args.AddRange(["--device", settings.Devices]);
            if (profile.GpuLayers == -1) args.AddRange(["--fit", "on", "--fit-target", settings.DeviceReserveMiB.ToString()]);
            else if (settings.TensorSplit.Length > 0) args.AddRange(["--tensor-split", settings.TensorSplit]);
        }
        return new(Path.Combine(profile.RuntimeDirectory, GetExecutableName(profile.Role)), args.ToArray());
    }

    /// <summary>Проверяет имя узла и различает порт управления и порт вычислений</summary>
    public static bool IsValidNode(NodeEndpoint node) => !string.IsNullOrWhiteSpace(node.Name)
        && Uri.CheckHostName(node.Host) != UriHostNameType.Unknown && IsPort(node.AgentPort) && IsPort(node.RpcPort)
        && node.AgentPort != node.RpcPort;

    // Ограничивает порт диапазоном TCP
    private static bool IsPort(int port) => port is >= 1 and <= 65535;

    // Сохраняет десятичную точку независимо от языка ОС
    private static string Invariant(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Читает только заголовок и не объявляет GGUF совместимым с движком
    private static async Task ValidateModelAsync(string path, List<Diagnostic> results, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) { results.Add(new("ModelMissing", true, path)); return; }
            await using var stream = File.OpenRead(path);
            var header = new byte[8];
            var read = await stream.ReadAtLeastAsync(header, 8, throwOnEndOfStream: false, cancellationToken: ct);
            var valid = read == 8 && header.AsSpan(0, 4).SequenceEqual("GGUF"u8) && BitConverter.ToUInt32(header, 4) is 2 or 3;
            results.Add(new(valid ? "GgufHeaderFound" : "InvalidGguf", !valid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("GGUF: {0}", ex);
            results.Add(new("ModelReadFailed", true, ex.Message));
        }
    }
}
