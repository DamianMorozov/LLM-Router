namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private readonly LocalAgentHost _localAgent;
    private readonly IAspNetRuntimeInstaller _runtimeInstaller;
    private readonly NetworkPortProbe _portProbe = new();
    private readonly CancellationTokenSource _agentLifetime = new();
    private bool _disposed;
    private string _missingRuntimeArchitecture = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstallAspNetRuntime))]
    [NotifyCanExecuteChangedFor(nameof(InstallAspNetRuntimeCommand))]
    private bool _isAspNetRuntimeMissing;

    [ObservableProperty] private string _localAgentListenAddress = "127.0.0.1";
    [ObservableProperty] private int _localAgentPort = 5100;
    [ObservableProperty] private bool _allowAgentLan;
    [ObservableProperty] private string _localAgentToken = "";
    [ObservableProperty] private bool _useAgentTls;
    [ObservableProperty] private bool _allowRemoteControl;
    [ObservableProperty] private string _localCertificateFingerprint = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AgentTokenMask))] private bool _showLocalAgentToken;
    [ObservableProperty] private string _localAgentStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditLocalAgent), nameof(CanConfigureFirewall), nameof(CanInstallAspNetRuntime))]
    [NotifyCanExecuteChangedFor(nameof(StartLocalAgentCommand), nameof(StopLocalAgentCommand), nameof(GenerateAgentTokenCommand), nameof(EnsureAgentFirewallCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallAspNetRuntimeCommand))]
    private bool _isLocalAgentBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditLocalAgent))]
    [NotifyCanExecuteChangedFor(nameof(StartLocalAgentCommand), nameof(StopLocalAgentCommand), nameof(GenerateAgentTokenCommand))]
    private bool _isLocalAgentRunning;

    public bool CanEditLocalAgent => !IsPreparingUpdate && !IsLocalAgentBusy && !IsLocalAgentRunning && !IsLocalModelBusy;
    public bool CanConfigureFirewall => OperatingSystem.IsWindows() && !IsLocalAgentBusy;
    public string LocalAddresses => NetworkPortProbe.GetLocalAddresses();
    public char AgentTokenMask => ShowLocalAgentToken ? '\0' : '●';
    public bool CanInstallAspNetRuntime => IsAspNetRuntimeMissing && OperatingSystem.IsWindows() && !IsLocalAgentBusy;

    // При переключении LAN выбирает подходящий адрес; пользователь может уточнить интерфейс
    partial void OnAllowAgentLanChanged(bool value)
    {
        if (!value) LocalAgentListenAddress = "127.0.0.1";
        else if (LocalAgentListenAddress is "127.0.0.1" or "::1") LocalAgentListenAddress = "0.0.0.0";
    }

    // Генерирует ключ в памяти текущего сеанса
    [RelayCommand(CanExecute = nameof(CanEditLocalAgent))]
    private void GenerateAgentToken() => LocalAgentToken = LocalAgentHost.GenerateToken();

    // Проверяет готовность процесса реальным авторизованным запросом
    [RelayCommand(CanExecute = nameof(CanEditLocalAgent))]
    private async Task StartLocalAgentAsync(CancellationToken ct)
    {
        IsLocalAgentBusy = true;
        LocalAgentStatus = Text["LocalAgentStarting"];
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
            if ((AllowAgentLan || AllowRemoteControl) && string.IsNullOrWhiteSpace(LocalAgentToken)) LocalAgentToken = LocalAgentHost.GenerateToken();
            if (UseAgentTls) LocalCertificateFingerprint = _localAgent.CertificateStore.Fingerprint();
            var settings = new AgentSettings { ListenAddress = LocalAgentListenAddress.Trim(), Port = LocalAgentPort, AllowLan = AllowAgentLan,
                UseTls = UseAgentTls, AllowRemoteControl = AllowRemoteControl, CertificateFingerprint = LocalCertificateFingerprint,
                LocalModelPath = LocalModelPath.Trim() };
            var snapshot = await _localAgent.StartAsync(settings, LocalAgentToken, linked.Token);
            IsLocalAgentRunning = _localAgent.IsRunning;
            IsAspNetRuntimeMissing = false;
            LocalAgentStatus = Text["LocalAgentReady"] + $" · {settings.ListenAddress}:{settings.Port} · {snapshot.HostName}";
            Events.Add(LocalAgentStatus);
        }
        catch (AspNetRuntimeMissingException ex)
        {
            _missingRuntimeArchitecture = ex.Architecture;
            IsAspNetRuntimeMissing = true;
            LocalAgentStatus = Text["AspNetRuntimeMissing"] + " · " + ex.Architecture;
            Status = LocalAgentStatus;
            Events.Add(LocalAgentStatus);
            Trace.TraceError("Agent runtime missing: {0}", ex.DiagnosticOutput);
        }
        catch (OperationCanceledException) { LocalAgentStatus = Text["AgentActionCancelled"]; }
        catch (Exception ex) { LocalAgentStatus = Text["OperationFailed"] + " · " + ex.Message; ReportError(ex); }
        finally { IsLocalAgentBusy = false; }
    }

    // Загружает и запускает официальный установщик после явного нажатия кнопки восстановления
    [RelayCommand(CanExecute = nameof(CanInstallAspNetRuntime))]
    private async Task InstallAspNetRuntimeAsync(CancellationToken ct)
    {
        IsLocalAgentBusy = true;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
            var progress = new Progress<RuntimeInstallProgress>(value =>
            {
                if (!_disposed && IsLocalAgentBusy)
                    LocalAgentStatus = Text[value.Stage] + (value.Package.Length > 0 ? " · " + value.Package : "")
                        + (value.Stage == "RuntimeDownloading" ? $" · {value.Percent}%" : "");
            });
            var result = await _runtimeInstaller.InstallAsync(_missingRuntimeArchitecture, progress, linked.Token);
            IsAspNetRuntimeMissing = false;
            LocalAgentStatus = Text[result.RestartRequired ? "RuntimeInstalledRestart" : "RuntimeInstalledRetry"] + " · " + result.Version;
            Status = LocalAgentStatus;
            Events.Add(LocalAgentStatus);
        }
        catch (OperationCanceledException) { LocalAgentStatus = Text["RuntimeInstallCancelled"]; }
        catch (Exception ex) { LocalAgentStatus = Text["RuntimeInstallFailed"] + " · " + ex.Message; ReportError(ex); }
        finally { IsLocalAgentBusy = false; }
    }

    // Останавливает собственный процесс без изменения сохранённой сетевой конфигурации
    [RelayCommand(CanExecute = nameof(CanStopLocalAgent))]
    private void StopLocalAgent()
    {
        _localAgent.Stop();
        IsLocalAgentRunning = false;
        LocalAgentStatus = Text["LocalAgentStopped"];
        Events.Add(LocalAgentStatus);
    }

    // Разрешает остановку после завершения проверки готовности
    private bool CanStopLocalAgent() => IsLocalAgentRunning && !IsLocalAgentBusy;

    // Проверяет TCP локально; результат не объявляет порт доступным с другого компьютера
    [RelayCommand]
    private async Task CheckLocalAgentPortAsync(CancellationToken ct)
    {
        try
        {
            var host = NetworkPortProbe.LocalProbeAddress(LocalAgentListenAddress.Trim());
            var result = await _portProbe.CheckAsync(host, LocalAgentPort, ct);
            LocalAgentStatus = Text[result.MessageKey] + $" · {host}:{LocalAgentPort} · " + Text["LocalPortCheckOnly"];
        }
        catch (Exception ex) { LocalAgentStatus = Text["OperationFailed"] + " · " + ex.Message; ReportError(ex); }
    }

    // Создаёт ограниченное правило только по явному нажатию кнопки
    [RelayCommand(CanExecute = nameof(CanConfigureFirewall))]
    private async Task EnsureAgentFirewallAsync(CancellationToken ct)
    {
        IsLocalAgentBusy = true;
        try
        {
            await new WindowsFirewall().EnsureRuleAsync(LocalAgentPort, ct);
            LocalAgentStatus = Text["AgentFirewallReady"] + $" · TCP {LocalAgentPort}";
            Events.Add(LocalAgentStatus);
        }
        catch (OperationCanceledException) { LocalAgentStatus = Text["AgentActionCancelled"]; }
        catch (Exception ex) { LocalAgentStatus = Text["OperationFailed"] + " · " + ex.Message; ReportError(ex); }
        finally { IsLocalAgentBusy = false; }
    }

    // Проверяет порт управления на выбранном удалённом узле отдельно от авторизации
    [RelayCommand(CanExecute = nameof(IsCoordinatorRole))]
    private Task CheckNodeAgentPortAsync(NodeViewModel? node, CancellationToken ct) => CheckNodePortAsync(node, rpc: false, ct);

    // Проверяет вычислительный TCP-порт, не объявляя RPC-протокол работоспособным
    [RelayCommand(CanExecute = nameof(IsCoordinatorRole))]
    private Task CheckNodeRpcPortAsync(NodeViewModel? node, CancellationToken ct) => CheckNodePortAsync(node, rpc: true, ct);

    // Показывает результат TCP-проверки в карточке узла
    private async Task CheckNodePortAsync(NodeViewModel? node, bool rpc, CancellationToken ct)
    {
        if (node is null) return;
        try
        {
            var port = rpc ? node.RpcPort : node.AgentPort;
            var result = await _portProbe.CheckAsync(node.Host.Trim(), port, ct);
            node.Status = Text[result.MessageKey] + $" · {node.Host}:{port} · " + Text["TcpOnly"];
        }
        catch (Exception ex) { node.Status = Text["ConnectionFailed"] + " · " + ex.Message; ReportError(ex); }
    }

    // Переносит уведомление фонового процесса на поток интерфейса
    private void OnLocalAgentExited(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || IsLocalAgentBusy || _localAgent.IsRunning || !IsLocalAgentRunning) return;
        IsLocalAgentRunning = false;
        LocalAgentStatus = Text["LocalAgentExited"];
        Events.Add(LocalAgentStatus);
    });

    /// <summary>Отменяет незавершённый запуск и останавливает агент при закрытии окна</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Updates.Dispose();
        StopMonitoring();
        CompleteModelLoading("ModelNotLoaded");
        _agentLifetime.Cancel();
        _engineOperation?.Cancel();
        _engine.OutputReceived -= OnEngineOutput;
        _engine.Exited -= OnEngineExited;
        _engine.Dispose();
        IsEngineRunning = false;
        IsEngineStarting = false;
        IsEngineBusy = false;
        _localAgent.Exited -= OnLocalAgentExited;
        _localAgent.Dispose();
        _agentLifetime.Dispose();
    }
}
