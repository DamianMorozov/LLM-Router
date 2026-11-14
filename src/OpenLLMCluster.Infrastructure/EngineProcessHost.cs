namespace OpenLLMCluster.Infrastructure;

/// <summary>Управляет только собственным llama-server или RPC-процессом и подтверждает готовность по сети</summary>
public sealed class EngineProcessHost(EngineCommandRunner? runner = null, EngineApiClient? api = null) : IDisposable
{
    private readonly EngineCommandRunner _runner = runner ?? new();
    private readonly EngineApiClient _api = api ?? new();
    private readonly ConcurrentQueue<string> _output = new();
    private Process? _process;
    private readonly object _processGate = new();
    private int _generation;
    private bool _disposed;
    private bool _starting;
    private string _apiKey = "";
    private readonly InferenceGatewayHost _gateway = new();
    public event Action<string>? OutputReceived;
    public event EventHandler? Exited;
    public bool IsRunning { get { lock (_processGate) return _process is { HasExited: false }; } }
    public string DiagnosticOutput => string.Join(Environment.NewLine, _output);
    public Uri? BaseUri { get; private set; }
    public ModelCapabilities Capabilities { get; private set; } = new(null, []);

    /// <summary>Проверяет файлы, CLI и RPC-протокол, запускает процесс и ждёт готовности с возможностью отмены</summary>
    public async Task StartAsync(RouterProfile profile, string apiKey, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) throw new InvalidOperationException("Движок уже запущен");
        if (profile.Role == NodeRole.Coordinator && string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Задай ключ inference API");
        Stop();
        int generation;
        lock (_processGate) { ObjectDisposedException.ThrowIf(_disposed, this); generation = _generation; _starting = true; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.StartupTimeoutSeconds));
        try
        {
            var errors = (await new RuntimePlanner().ValidateAsync(profile, timeout.Token)).Where(result => result.IsError).ToArray();
            if (errors.Length > 0) throw new InvalidOperationException("Параметры запуска не прошли проверку: " + string.Join(", ", errors.Select(item => item.Code)));
            LlamaPackageInstaller.ValidateTag(profile.LlamaReleaseTag);
            var version = await _runner.ValidateBuildAsync(profile.RuntimeDirectory, timeout.Token);
            LlamaPackageInstaller.RequireBuildVersion(version, profile.LlamaReleaseTag);
            if (profile.Role == NodeRole.Coordinator && profile.Nodes.Any(node => node.Enabled))
            {
                var nodes = string.Join(',', profile.Nodes.Where(node => node.Enabled).Select(node => node.RpcEndpoint));
                var devices = await _runner.RunAsync(new(Path.Combine(profile.RuntimeDirectory, RuntimePlanner.GetExecutableName(NodeRole.Coordinator)),
                    ["--rpc", nodes, "--list-devices"]), timeout.Token);
                OutputReceived?.Invoke(devices.Trim());
                var confirmed = ClusterDevicePlanner.Parse(devices);
                foreach (var node in profile.Nodes.Where(node => node.Enabled))
                    if (!confirmed.Any(device => device.Description.Contains(node.RpcEndpoint, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("RPC не подтвердил устройство узла " + node.Name + ". Проверь версию и журнал RPC");
            }
            EnsurePortFree(profile.BindAddress, profile.Role == NodeRole.Worker ? profile.RpcPort : profile.ApiPort);
            var backendProfile = profile;
            if (profile.Role == NodeRole.Coordinator)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                backendProfile = profile with { BindAddress = "127.0.0.1", ApiPort = ((IPEndPoint)listener.LocalEndpoint).Port };
            }
            var plan = new RuntimePlanner().Build(backendProfile);
            _apiKey = apiKey;
            _output.Clear();
            var start = _runner.StartFactory(plan);
            start.Environment["LLAMA_API_KEY"] = apiKey;
            if (profile.Role == NodeRole.Worker) start.Environment["LLAMA_CACHE"] = RpcModelCache.DefaultCacheRoot;
            Process process;
            var host = NetworkPortProbe.LocalProbeAddress(profile.BindAddress);
            lock (_processGate)
            {
                // Не создаёт модель после закрытия владельца или остановки во время проверки CLI
                ObjectDisposedException.ThrowIf(_disposed, this);
                timeout.Token.ThrowIfCancellationRequested();
                if (generation != _generation) throw new OperationCanceledException("Запуск движка отменён остановкой");
                process = new Process { StartInfo = start, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, args) => Capture(args.Data);
                process.ErrorDataReceived += (_, args) => Capture(args.Data);
                process.Exited += (_, _) => OnProcessExited(process);
                try { process.Start(); }
                catch { process.Dispose(); throw; }
                _process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                BaseUri = profile.Role == NodeRole.Coordinator ? new UriBuilder("http", host, profile.ApiPort).Uri : null;
            }
            if (profile.Role == NodeRole.Coordinator)
            {
                _gateway.OutputReceived -= Capture;
                _gateway.OutputReceived += Capture;
                _gateway.Exited -= OnGatewayExited;
                _gateway.Exited += OnGatewayExited;
                Task gatewayStart;
                lock (_processGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (generation != _generation) throw new OperationCanceledException("Запуск движка отменён остановкой");
                    gatewayStart = _gateway.StartAsync(profile, new UriBuilder("http", "127.0.0.1", backendProfile.ApiPort).Uri, apiKey, timeout.Token);
                }
                await gatewayStart;
            }
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) { process.WaitForExit(); throw new InvalidOperationException("Движок завершился при запуске: " + DiagnosticOutput); }
                var ready = profile.Role == NodeRole.Worker
                    ? (await new NetworkPortProbe().CheckAsync(host, profile.RpcPort, timeout.Token)).IsOpen
                    : await _api.IsReadyAsync(BaseUri!, apiKey, timeout.Token);
                if (process.HasExited) continue;
                if (ready)
                {
                    if (BaseUri is not null) Capabilities = await _api.ReadCapabilitiesAsync(BaseUri, apiKey, timeout.Token);
                    return;
                }
                await Task.Delay(250, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        { Stop(); throw new TimeoutException("Истекло время ожидания движка; проверь журнал и увеличь таймаут загрузки модели"); }
        catch (Exception ex) { Stop(); Trace.TraceError("Запуск движка: {0}", ex.GetType().Name); throw; }
        finally { lock (_processGate) _starting = false; }
    }

    /// <summary>Выполняет контрольный запрос только к запущенному этим экземпляром серверу</summary>
    public Task<EngineTestResult> TestAsync(string prompt, CancellationToken ct = default)
    {
        if (!IsRunning || BaseUri is null) throw new InvalidOperationException("Сначала запусти главный сервер и дождись готовности модели");
        return _api.TestAsync(BaseUri, _apiKey, prompt, ct);
    }

    /// <summary>Завершает собственное дерево процессов, освобождая порт и ресурсы модели</summary>
    public void Stop()
    {
        lock (_processGate)
        {
            _generation++;
            _gateway.Stop();
            var process = _process;
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                        if (!process.WaitForExit(5000)) throw new TimeoutException("Движок не завершился; модель ещё не выгружена");
                    }
                }
                catch (Exception ex) { Trace.TraceError("Остановка движка: {0}", ex); throw; }
                process.Dispose();
                _process = null;
            }
            BaseUri = null;
            Capabilities = new(null, []);
        }
    }

    /// <summary>Останавливает вычислительный процесс при закрытии Desktop</summary>
    public void Dispose() { lock (_processGate) { _disposed = true; Stop(); } }

    // Закрывает внешний порт, если вычислительный процесс завершился самостоятельно
    private void OnProcessExited(Process process) => ThreadPool.QueueUserWorkItem(_ =>
    {
        lock (_processGate)
        {
            if (_starting || !ReferenceEquals(_process, process)) return;
            _gateway.Stop();
            Exited?.Invoke(this, EventArgs.Empty);
        }
    });

    // При потере шлюза останавливает модель, чтобы UI не показывал недоступный inference как готовый
    private void OnGatewayExited(object? sender, EventArgs args)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_processGate)
            {
                if (_gateway.IsRunning || _process is null || _disposed) return;
                Stop();
                Exited?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    // Отказывается от занятого порта, не объявляя чужой процесс собственным движком
    private static void EnsurePortFree(string address, int port)
    {
        using var listener = new TcpListener(IPAddress.Parse(address), port);
        try { listener.Start(); }
        catch (SocketException ex) { throw new InvalidOperationException("TCP-порт " + port + " занят или недоступен для прослушивания", ex); }
    }

    // Ограничивает журнал и скрывает текущий ключ API до передачи в интерфейс
    private void Capture(string? line)
    {
        if (line is null) return;
        if (_apiKey.Length > 0) line = line.Replace(_apiKey, "[key]", StringComparison.Ordinal);
        _output.Enqueue(line);
        while (_output.Count > 500) _output.TryDequeue(out _);
        OutputReceived?.Invoke(line);
    }
}
