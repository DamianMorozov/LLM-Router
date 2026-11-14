namespace OpenLLMCluster.Infrastructure;

/// <summary>Запускает отдельный диагностический агент из пакета Desktop и завершает только собственный процесс</summary>
public sealed class LocalAgentHost(string? executablePath = null, AgentCertificateStore? certificateStore = null) : IDisposable
{
    public AgentCertificateStore CertificateStore { get; } = certificateStore ?? new();
    private readonly string _executablePath = executablePath ?? Path.Combine(AppContext.BaseDirectory, "Agent",
        OperatingSystem.IsWindows() ? "OpenLLMCluster.Agent.exe" : "OpenLLMCluster.Agent");
    private readonly ConcurrentQueue<string> _output = new();
    private Process? _process;
    private bool _disposed;
    public event EventHandler? Exited;
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Создаёт случайный ключ для передачи агенту только через окружение процесса</summary>
    public static string GenerateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Проверяет настройки запуска и обязательный ключ при доступе из сети</summary>
    public static void ValidateSettings(AgentSettings settings, string token)
    {
        if (!IPAddress.TryParse(settings.ListenAddress, out var address) || settings.Port is < 1 or > 65535)
            throw new ArgumentException("Укажи корректный IP-адрес прослушивания и порт от 1 до 65535");
        if (!IPAddress.IsLoopback(address) && (!settings.AllowLan || token.Length < 24))
            throw new ArgumentException("Для доступа из сети включи LAN и задай ключ длиной не менее 24 символов");
        if (settings.AllowRemoteControl && (!settings.UseTls || token.Length < 24))
            throw new ArgumentException("Удалённое управление требует TLS и ключ длиной не менее 24 символов");
    }

    /// <summary>Запускает агент и подтверждает его готовность авторизованным запросом MagicOnion</summary>
    public async Task<NodeSnapshot> StartAsync(AgentSettings settings, string token, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateSettings(settings, token);
        if (IsRunning) throw new InvalidOperationException("Локальный агент уже запущен");
        Stop();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(_executablePath))
                throw new FileNotFoundException("Не найден Agent в пакете Desktop. Выполни публикацию и скопируй всю папку приложения", _executablePath);
            _output.Clear();
            var fingerprint = settings.UseTls ? CertificateStore.Fingerprint() : "";
            var start = CreateStartInfo(settings, token);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _process = process;
            process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
            process.OutputDataReceived += (_, args) => CaptureOutput(args.Data, token);
            process.ErrorDataReceived += (_, args) => CaptureOutput(args.Data, token);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));
            var host = NetworkPortProbe.LocalProbeAddress(settings.ListenAddress);
            var node = new NodeEndpoint { Name = "local", Host = host, AgentPort = settings.Port, RpcPort = settings.Port == 50052 ? 50053 : 50052,
                UseTls = settings.UseTls, CertificateFingerprint = fingerprint };
            while (true)
            {
                if (process.HasExited) throw CreateExitException(process);
                try
                {
                    var snapshot = await new NodeAgentClient().ProbeAsync(node, token, timeout.Token);
                    if (process.HasExited) throw CreateExitException(process);
                    return snapshot;
                }
                catch (RpcException) when (!timeout.IsCancellationRequested) { }
                await Task.Delay(150, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Stop();
            throw new TimeoutException("Агент не подтвердил готовность за 18 секунд");
        }
        catch (Exception ex)
        {
            Stop();
            Trace.TraceError("Start local agent: {0}", ex.GetType().Name);
            throw;
        }
    }

    /// <summary>Останавливает только агент, запущенный этим экземпляром Desktop</summary>
    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
        }
        catch (Exception ex) { Trace.TraceError("Stop local agent: {0}", ex.Message); }
        finally { process.Dispose(); }
    }

    /// <summary>Завершает принадлежащий приложению агент при закрытии Desktop</summary>
    public void Dispose() { _disposed = true; Stop(); }

    // Дожидается чтения stderr/stdout, чтобы короткоживущий apphost не терял сообщение об отсутствующем runtime
    private Exception CreateExitException(Process process)
    {
        process.WaitForExit();
        return AspNetRuntimeMissingException.FromAgentExit(process.ExitCode, string.Join(Environment.NewLine, _output));
    }

    // Формирует аргументы без секретов; ключ передаётся через окружение
    private ProcessStartInfo CreateStartInfo(AgentSettings settings, string token)
    {
        var isAssembly = _executablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(isAssembly ? "dotnet" : _executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_executablePath))!,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (isAssembly) start.ArgumentList.Add(_executablePath);
        start.Environment["Router__ListenAddress"] = settings.ListenAddress;
        start.Environment["Router__Port"] = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Router__AccessToken"] = token;
        start.Environment["Router__AllowInsecureLan"] = settings.AllowLan.ToString();
        start.Environment["Router__UseTls"] = settings.UseTls.ToString();
        start.Environment["Router__AllowRemoteControl"] = settings.AllowRemoteControl.ToString();
        start.Environment["Router__CertificatePath"] = CertificateStore.FilePath;
        start.Environment["Router__LocalModelPath"] = settings.LocalModelPath;
        return start;
    }

    // Ограничивает объём диагностики и исключает ключ из сообщений процесса
    private void CaptureOutput(string? line, string token)
    {
        if (line is null) return;
        _output.Enqueue(token.Length > 0 ? line.Replace(token, "[token]") : line);
        while (_output.Count > 40) _output.TryDequeue(out _);
    }
}
