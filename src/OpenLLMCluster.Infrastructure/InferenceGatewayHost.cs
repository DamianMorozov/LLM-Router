namespace OpenLLMCluster.Infrastructure;

/// <summary>Запускает HTTP-шлюз из уже поставляемого Agent, не добавляя ASP.NET-зависимость в Desktop</summary>
public sealed class InferenceGatewayHost : IDisposable
{
    private Process? _process;
    private readonly object _gate = new();
    private int _generation;
    private volatile bool _stopping;
    private readonly ConcurrentQueue<string> _lines = new();
    public event Action<string>? OutputReceived;
    public event EventHandler? Exited;
    public bool IsRunning { get { lock (_gate) return _process is { HasExited: false }; } }

    /// <summary>Запускает управляемый процесс и подтверждает авторизованную готовность его HTTP-порта</summary>
    public async Task StartAsync(RouterProfile profile, Uri backend, string apiKey, CancellationToken ct = default)
    {
        int generation;
        lock (_gate) generation = _generation;
        var packaged = Path.Combine(AppContext.BaseDirectory, "Agent", "OpenLLMCluster.Agent.dll");
        var assembly = File.Exists(packaged) ? packaged : Path.Combine(AppContext.BaseDirectory, "OpenLLMCluster.Agent.dll");
        if (!File.Exists(assembly)) throw new FileNotFoundException("Для inference-шлюза нужен Agent из пакета приложения", assembly);
        var executable = Path.ChangeExtension(assembly, OperatingSystem.IsWindows() ? ".exe" : null);
        var useAppHost = File.Exists(executable);
        var start = new ProcessStartInfo(useAppHost ? executable : "dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!useAppHost) start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--inference-gateway");
        start.Environment["Gateway__Address"] = profile.BindAddress;
        start.Environment["Gateway__Port"] = profile.ApiPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Gateway__Backend"] = backend.ToString();
        start.Environment["Gateway__Token"] = apiKey;
        Process process;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (generation != _generation) throw new OperationCanceledException("Запуск шлюза отменён");
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => Capture(args.Data);
            process.ErrorDataReceived += (_, args) => Capture(args.Data);
            process.Exited += (_, _) => { if (!_stopping && ReferenceEquals(_process, process)) Exited?.Invoke(this, EventArgs.Empty); };
            try { process.Start(); } catch { process.Dispose(); throw; }
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        var ready = new UriBuilder("http", NetworkPortProbe.LocalProbeAddress(profile.BindAddress), profile.ApiPort, "olc/gateway-ready").Uri;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                process.WaitForExit();
                throw AspNetRuntimeMissingException.FromAgentExit(process.ExitCode, string.Join(Environment.NewLine, _lines));
            }
            try
            {
                using var response = await http.GetAsync(ready, ct);
                if (response.StatusCode == HttpStatusCode.NoContent) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(100, ct);
        }
    }

    // Передаёт только служебные маркеры; тела и ключи не попадают в журнал Desktop
    private void Capture(string? line)
    {
        if (line is null) return;
        _lines.Enqueue(line);
        while (_lines.Count > 40) _lines.TryDequeue(out _);
        if (line.StartsWith("OLC_EFFORT_", StringComparison.Ordinal)) OutputReceived?.Invoke(line);
    }

    /// <summary>Завершает только собственный шлюз и подтверждает освобождение процесса</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _generation++;
            if (_process is not { } process) return;
            _stopping = true;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5000)) throw new TimeoutException("Inference-шлюз не завершился");
                }
                process.Dispose();
                _process = null;
            }
            catch (Exception ex) { Trace.TraceError("Остановка inference-шлюза: {0}", ex.GetType().Name); throw; }
            finally { _stopping = false; }
        }
    }

    /// <summary>Освобождает управляемый HTTP-процесс</summary>
    public void Dispose() => Stop();
}
