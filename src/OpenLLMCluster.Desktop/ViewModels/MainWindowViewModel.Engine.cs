namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private readonly ILlamaPackageInstaller _llamaInstaller;
    private readonly EngineProcessHost _engine;
    private CancellationTokenSource? _engineOperation;
    private NodeRole? _runningRole;
    private string _engineStatusKey = "EngineStopped";
    private string _engineStatusDetail = "";
    public string[] LlamaBackends { get; } = ["CUDA", "CPU", "Metal"];
    public ObservableCollection<string> EngineEvents { get; } = [];
    [ObservableProperty] private string _llamaReleaseTag = LlamaBuildDefaults.ReleaseTag;
    [ObservableProperty] private int _llamaBackendIndex;
    [ObservableProperty] private int _gpuLayers = -1;
    [ObservableProperty] private int _engineStartupTimeoutSeconds = 900;
    [ObservableProperty] private string _serverApiKey = "";
    [ObservableProperty] private string _engineTestPrompt = "Ответь одним словом: READY";
    [ObservableProperty] private string _engineTestOutput = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditEngine), nameof(CanStopEngine), nameof(CanTestEngine))]
    [NotifyCanExecuteChangedFor(nameof(InstallLlamaCommand), nameof(StartEngineCommand), nameof(StopEngineCommand), nameof(TestEngineCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckClusterCommand), nameof(SuggestGpuSplitCommand), nameof(BenchmarkEngineCommand))]
    private bool _isEngineBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditEngine), nameof(CanStopEngine), nameof(CanTestEngine))]
    [NotifyCanExecuteChangedFor(nameof(InstallLlamaCommand), nameof(StartEngineCommand), nameof(StopEngineCommand), nameof(TestEngineCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckClusterCommand), nameof(SuggestGpuSplitCommand), nameof(BenchmarkEngineCommand))]
    private bool _isEngineRunning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStopEngine), nameof(IsModelLoading))]
    [NotifyCanExecuteChangedFor(nameof(StopEngineCommand))]
    private bool _isEngineStarting;
    public bool CanEditEngine => !IsEngineBusy && !IsEngineRunning;
    public bool CanEditCoordinatorSettings => CanEditEngine && IsCoordinatorRole;
    public bool CanEditWorkerSettings => CanEditEngine && IsWorkerRole;
    public bool CanConfigureRpcFirewall => IsWorkerRole && CanConfigureFirewall;
    public string RoleSettingsHelp => Text[IsCoordinatorRole ? "CoordinatorSettingsHelp" : "WorkerSettingsHelp"];
    public bool IsWorkerRole => RoleIndex == (int)NodeRole.Worker;
    public bool IsCoordinatorRole => RoleIndex == (int)NodeRole.Coordinator;
    public bool CanStopEngine => IsEngineRunning || IsEngineStarting;
    public bool CanTestEngine => IsEngineRunning && !IsEngineBusy && _runningRole == NodeRole.Coordinator;
    public string EngineStatus => Text[_engineStatusKey] + (_engineStatusDetail.Length > 0 ? " · " + _engineStatusDetail : "");

    // Загружает официальные пакеты в пользовательский каталог без изменения установленной LM Studio
    [RelayCommand(CanExecute = nameof(CanEditEngine))]
    private async Task InstallLlamaAsync(CancellationToken ct)
    {
        IsEngineBusy = true;
        _engineOperation = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
        try
        {
            SetEngineStatus("LlamaMetadata");
            var progress = new Progress<LlamaInstallProgress>(value =>
            {
                if (!_disposed && IsEngineBusy) SetEngineStatus(value.Stage, value.File + (value.Stage == "LlamaDownloading" ? $" · {value.Percent}%" : ""));
            });
            var installed = await _llamaInstaller.InstallAsync(LlamaReleaseTag.Trim(), (LlamaBackend)LlamaBackendIndex, progress, _engineOperation.Token);
            RuntimeDirectory = installed.Directory;
            SelectedRuntimeLocation = null;
            RuntimeLocations.Clear();
            OnPropertyChanged(nameof(HasRuntimeLocations));
            if (installed.Backend == LlamaBackend.Cpu) GpuLayers = 0;
            else if (GpuLayers == 0) GpuLayers = -1;
            SetEngineStatus("LlamaInstalled", installed.ReleaseTag + " · " + installed.Backend);
            Events.Add(EngineStatus);
        }
        catch (OperationCanceledException) { SetEngineStatus("AgentActionCancelled"); }
        catch (Exception ex) { SetEngineStatus("OperationFailed", ex.Message); ReportError(ex); }
        finally { IsEngineBusy = false; _engineOperation.Dispose(); _engineOperation = null; }
    }

    // Проверяет версии узлов и запускает отдельный вычислительный процесс для выбранной роли
    [RelayCommand(CanExecute = nameof(CanEditEngine))]
    private async Task StartEngineAsync(CancellationToken ct)
    {
        IsEngineBusy = true;
        IsEngineStarting = true;
        EngineTestOutput = "";
        ObserveEffort("OLC_EFFORT_DEFAULT");
        _engineOperation = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
        try
        {
            var profile = CreateProfile();
            SetEngineStatus("EngineStarting");
            BeginModelLoading();
            if (profile.Role == NodeRole.Coordinator)
            {
                if (string.IsNullOrWhiteSpace(ServerApiKey)) ServerApiKey = LocalAgentHost.GenerateToken();
                LocalModelIdentity? modelIdentity = null;
                foreach (var node in Nodes.Where(node => node.Enabled))
                {
                    var snapshot = await _agent.ProbeAsync(node.ToEndpoint(), node.AccessToken, _engineOperation.Token);
                    ClusterBuildGuard.RequireCompatible(profile.LlamaReleaseTag, LlamaInstallationStore.ReadDirectory(profile.RuntimeDirectory), snapshot, node.Name);
                    if (snapshot.LocalModelConfigured)
                    {
                        SetEngineStatus("LocalModelHashing", node.Name);
                        SetModelLoadStage("LocalModelHashing");
                        modelIdentity ??= await Task.Run(() => RpcModelCache.IdentifyAsync(profile.ModelPath, _engineOperation.Token), _engineOperation.Token);
                        SetEngineStatus("LocalModelPreparing", node.Name);
                        SetModelLoadStage("LocalModelPreparing");
                        await CheckNodeLocalModelAsync(node, modelIdentity, _engineOperation.Token);
                    }
                }
            }
            _runningRole = profile.Role;
            await _engine.StartAsync(profile, ServerApiKey, _engineOperation.Token);
            if (_disposed) return;
            ApplyCapabilities(_engine.Capabilities);
            IsEngineRunning = _engine.IsRunning;
            if (profile.Role == NodeRole.Coordinator) CompleteModelLoading("ModelReady");
            SetEngineStatus(profile.Role == NodeRole.Worker ? "RpcReady" : "InferenceReady",
                _engine.BaseUri is not null ? new Uri(_engine.BaseUri, "v1").ToString() : profile.BindAddress + ":" + profile.RpcPort);
            Events.Add(EngineStatus);
        }
        catch (AspNetRuntimeMissingException ex)
        {
            _missingRuntimeArchitecture = ex.Architecture;
            IsAspNetRuntimeMissing = true;
            SetEngineStatus("AspNetRuntimeMissing", ex.Architecture);
            CompleteModelLoading("ModelLoadFailed");
            Events.Add(EngineStatus);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) { SetEngineStatus("AgentActionCancelled"); CompleteModelLoading("ModelLoadCancelled"); }
        }
        catch (Exception ex)
        {
            if (!_disposed) { SetEngineStatus("OperationFailed", ex.Message); CompleteModelLoading("ModelLoadFailed"); ReportError(ex); }
        }
        finally
        {
            IsEngineRunning = _engine.IsRunning;
            IsEngineStarting = false;
            IsEngineBusy = false;
            _engineOperation.Dispose();
            _engineOperation = null;
        }
    }

    // Отменяет загрузку модели и останавливает только вычислительный процесс этого окна
    [RelayCommand(CanExecute = nameof(CanStopEngine))]
    private void StopEngine()
    {
        try
        {
            _engineOperation?.Cancel();
            _engine.Stop();
            IsEngineRunning = false;
            CompleteModelLoading("ModelNotLoaded");
            SetEngineStatus("EngineStopped");
            Events.Add(EngineStatus);
        }
        catch (Exception ex) { ReportError(ex); }
    }

    // Позволяет отменить загрузку пакетов или контрольный запрос
    [RelayCommand]
    private void CancelEngineOperation() => _engineOperation?.Cancel();

    // Подтверждает генерацию через OpenAI-совместимый API после успешного /health
    [RelayCommand(CanExecute = nameof(CanTestEngine))]
    private async Task TestEngineAsync(CancellationToken ct)
    {
        IsEngineBusy = true;
        _engineOperation = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
        try
        {
            var result = await _engine.TestAsync(EngineTestPrompt, _engineOperation.Token);
            EngineTestOutput = FormatMeasurement(result);
            SetEngineStatus("InferenceTestPassed");
            Events.Add(EngineStatus);
        }
        catch (OperationCanceledException) { SetEngineStatus("AgentActionCancelled"); }
        catch (Exception ex) { SetEngineStatus("OperationFailed", ex.Message); ReportError(ex); }
        finally { IsEngineBusy = false; _engineOperation.Dispose(); _engineOperation = null; }
    }

    // Создаёт отдельное правило для RPC, не меняя правило порта диагностического агента
    [RelayCommand(CanExecute = nameof(CanConfigureRpcFirewall))]
    private async Task EnsureRpcFirewallAsync(CancellationToken ct)
    {
        try { await new WindowsFirewall().EnsureRpcRuleAsync(RpcPort, ct); SetEngineStatus("RpcFirewallReady", RpcPort.ToString()); Events.Add(EngineStatus); }
        catch (OperationCanceledException) { SetEngineStatus("AgentActionCancelled"); }
        catch (Exception ex) { SetEngineStatus("OperationFailed", ex.Message); ReportError(ex); }
    }

    // Копирует ключ API только по явному нажатию кнопки
    [RelayCommand]
    private async Task CopyServerKeyAsync(IClipboard? clipboard)
    {
        if (clipboard is null) return;
        try { await clipboard.SetTextAsync(ServerApiKey); SetEngineStatus("AgentTokenCopied"); }
        catch (Exception ex) { ReportError(ex); }
    }

    // Сохраняет ключ локализации, чтобы текущий статус менял язык вместе с интерфейсом
    private void SetEngineStatus(string key, string detail = "")
    {
        _engineStatusKey = key;
        _engineStatusDetail = detail;
        OnPropertyChanged(nameof(EngineStatus));
    }

    // Передаёт строки фонового процесса в ограниченный журнал UI
    private void OnEngineOutput(string line) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        ObserveModelLoading(line);
        ObserveModelActivity(line);
        ObserveEffort(line);
        EngineEvents.Add(line);
        while (EngineEvents.Count > 500) EngineEvents.RemoveAt(0);
    });

    // Не оставляет интерфейс в состоянии готовности после аварийного завершения движка
    private void OnEngineExited(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || IsEngineStarting || _engine.IsRunning || !IsEngineRunning) return;
        IsEngineRunning = false;
        CompleteModelLoading("ModelLoadFailed");
        SetEngineStatus("EngineExited");
    });
}
