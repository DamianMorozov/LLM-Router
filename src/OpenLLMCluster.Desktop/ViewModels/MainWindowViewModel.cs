namespace OpenLLMCluster.Desktop.ViewModels;

/// <summary>Координирует профиль, диагностику, локальный агент и представление</summary>
public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ProfileStore _store;
    private readonly RuntimePlanner _planner = new();
    private readonly EnvironmentProbe _probe = new();
    private readonly NodeAgentClient _agent = new();
    private bool _profileLoadFailed;
    private bool _startupLoadFailed;
    public WindowGeometry WindowGeometry { get; set; } = new();
    public Localizer Text { get; } = new();
    public ApplicationUpdatesViewModel Updates { get; }
    public ObservableCollection<NodeViewModel> Nodes { get; } = [];
    public ObservableCollection<string> Checks { get; } = [];
    public ObservableCollection<string> Events { get; } = [];
    public LocalizedOption[] RoleOptions { get; }
    public LocalizedOption[] ThemeOptions { get; }
    public string ProfilePath => _store.FilePath;

    [ObservableProperty] private int _languageIndex;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWorkerRole), nameof(IsCoordinatorRole), nameof(IsCoordinatorMode), nameof(CanViewNodes))]
    private int _roleIndex;
    /// <summary>Включённое положение переключателя соответствует главному компьютеру</summary>
    public bool IsCoordinatorMode
    {
        get => RoleIndex == (int)NodeRole.Coordinator;
        set => RoleIndex = value ? (int)NodeRole.Coordinator : (int)NodeRole.Worker;
    }
    [ObservableProperty] private string _runtimeDirectory = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OverviewModelName))] private string _modelPath = "";
    [ObservableProperty] private string _bindAddress = "127.0.0.1";
    [ObservableProperty] private int _apiPort = 8080;
    [ObservableProperty] private int _rpcPort = 50052;
    [ObservableProperty] private int _contextLength = 8192;
    [ObservableProperty] private int _cpuThreads = Math.Max(1, Environment.ProcessorCount / 2);
    [ObservableProperty] private string _commandPreview = "";
    [ObservableProperty] private string _hardwareSummary = "";
    [ObservableProperty] private string _gpuSummary = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _rememberSettings = true;

    /// <summary>Создаёт модель представления с подменяемым хранилищем для проверок</summary>
    public MainWindowViewModel(ProfileStore store, LocalAgentHost? agentHost = null, IAspNetRuntimeInstaller? runtimeInstaller = null,
        LocalInstallationDiscovery? installationDiscovery = null, ILlamaPackageInstaller? llamaInstaller = null, EngineProcessHost? engineHost = null,
        ApplicationUpdateService? updateService = null)
    {
        _store = store;
        Text.SetLanguage("system");
        var updates = updateService ?? new ApplicationUpdateService();
        Updates = new ApplicationUpdatesViewModel(updates, Text,
            () => IsEngineBusy || IsEngineStarting || IsEngineRunning || IsLocalModelBusy || IsLocalAgentBusy || IsLocalAgentRunning,
            message => Events.Add(message), ct => InstallUpdateAsync(updates, ct));
        _llamaInstaller = llamaInstaller ?? new LlamaPackageInstaller();
        _engine = engineHost ?? new EngineProcessHost();
        _engine.OutputReceived += OnEngineOutput;
        _engine.Exited += OnEngineExited;
        _installationDiscovery = installationDiscovery ?? new LocalInstallationDiscovery();
        _localAgent = agentHost ?? new LocalAgentHost();
        _runtimeInstaller = runtimeInstaller ?? new AspNetRuntimeInstaller();
        _localAgent.Exited += OnLocalAgentExited;
        LocalAgentStatus = Text["LocalAgentStopped"];
        RoleOptions = [new(Text, "CoordinatorRole"), new(Text, "WorkerRole")];
        ThemeOptions = [new(Text, "ThemeSystem"), new(Text, "ThemeLight"), new(Text, "ThemeDark")];
        WorkloadOptions = [new(Text, "WorkloadCustom"), new(Text, "WorkloadInteractive"), new(Text, "WorkloadBalanced"),
            new(Text, "WorkloadThroughput"), new(Text, "WorkloadMaximum")];
        Status = Text["NotChecked"];
        Nodes.CollectionChanged += (_, args) =>
        {
            if (args.OldItems is not null)
                foreach (NodeViewModel node in args.OldItems) node.PropertyChanged -= OnNodeChanged;
            if (args.NewItems is not null)
                foreach (NodeViewModel node in args.NewItems) node.PropertyChanged += OnNodeChanged;
            CommandPreview = "";
        };
    }

    /// <summary>Сбрасывает предпросмотр при изменении параметров запуска</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.PropertyName is nameof(IsEngineBusy) or nameof(IsEngineStarting) or nameof(IsEngineRunning)
            or nameof(IsLocalModelBusy) or nameof(IsLocalAgentBusy) or nameof(IsLocalAgentRunning)) Updates?.Refresh();
        if (args.PropertyName is nameof(IsLocalModelBusy) or nameof(IsLocalAgentBusy) or nameof(IsLocalAgentRunning)
            or nameof(CanEditEngine) or nameof(RoleIndex))
        {
            OnPropertyChanged(nameof(CanEditLocalAgent));
            OnPropertyChanged(nameof(CanConfigureLocalModel));
            PrepareLocalModelCommand.NotifyCanExecuteChanged();
            StartLocalAgentCommand.NotifyCanExecuteChanged();
        }
        if (args.PropertyName is nameof(RoleIndex) or nameof(RuntimeDirectory) or nameof(ModelPath) or nameof(BindAddress)
            or nameof(ApiPort) or nameof(RpcPort) or nameof(ContextLength) or nameof(CpuThreads)) CommandPreview = "";
        if (args.PropertyName is nameof(GpuLayers) or nameof(EngineStartupTimeoutSeconds) or nameof(LlamaReleaseTag) or nameof(LlamaBackendIndex))
            CommandPreview = "";
        if (args.PropertyName is nameof(MaxOutputTokens) or nameof(Temperature) or nameof(TopK) or nameof(TopP) or nameof(MinP)
            or nameof(RepeatPenalty) or nameof(ReasoningMode) or nameof(ReasoningEffort) or nameof(ReasoningBudget) or nameof(ParallelRequests)
            or nameof(BatchSize) or nameof(MicroBatchSize) or nameof(FlashAttention) or nameof(CacheTypeK) or nameof(CacheTypeV)
            or nameof(Devices) or nameof(TensorSplit)) CommandPreview = "";
        if (args.PropertyName is nameof(CanEditEngine) or nameof(RoleIndex))
        {
            OnPropertyChanged(nameof(CanEditCoordinatorSettings));
            OnPropertyChanged(nameof(CanEditWorkerSettings));
            OnPropertyChanged(nameof(CanSelectEffort));
            OnPropertyChanged(nameof(CanFindModels));
            OnPropertyChanged(nameof(CanConfigureRpcFirewall));
            AddNodeCommand.NotifyCanExecuteChanged();
            RemoveNodeCommand.NotifyCanExecuteChanged();
            FindModelsCommand.NotifyCanExecuteChanged();
            CheckClusterCommand.NotifyCanExecuteChanged();
            SuggestGpuSplitCommand.NotifyCanExecuteChanged();
            EnsureRpcFirewallCommand.NotifyCanExecuteChanged();
        }
        if (args.PropertyName == nameof(RoleIndex))
        {
            RefreshReadinessLabels();
            OnPropertyChanged(nameof(RoleSettingsHelp));
            ProbeNodeCommand.NotifyCanExecuteChanged();
            PairNodeCommand.NotifyCanExecuteChanged();
            StartRemoteRpcCommand.NotifyCanExecuteChanged();
            StopRemoteRpcCommand.NotifyCanExecuteChanged();
            ReadRemoteRpcCommand.NotifyCanExecuteChanged();
            CheckNodeAgentPortCommand.NotifyCanExecuteChanged();
            CheckNodeRpcPortCommand.NotifyCanExecuteChanged();
        }
        if (args.PropertyName is nameof(CanEditEngine) or nameof(IsDiscovering))
        {
            OnPropertyChanged(nameof(CanFindRuntime));
            OnPropertyChanged(nameof(CanFindModels));
            FindRuntimeCommand.NotifyCanExecuteChanged();
            FindModelsCommand.NotifyCanExecuteChanged();
        }
        if (args.PropertyName == nameof(CanConfigureFirewall))
        { OnPropertyChanged(nameof(CanConfigureRpcFirewall)); EnsureRpcFirewallCommand.NotifyCanExecuteChanged(); }
        if (args.PropertyName == nameof(ModelPath)) ApplyCapabilities(new(null, []));
        OnWorkloadSettingChanged(args.PropertyName);
        if (args.PropertyName is nameof(IsEngineRunning) or nameof(IsEngineStarting) or nameof(ModelLoadStageText))
        {
            if (args.PropertyName == nameof(IsEngineRunning)) _modelActivity = new(ModelActivityKind.Unavailable);
            OnPropertyChanged(nameof(ModelActivityText));
        }
    }

    // Исключает устаревший предпросмотр после изменения адреса или состава кластера
    private void OnNodeChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(NodeViewModel.Host) or nameof(NodeViewModel.AgentPort) or nameof(NodeViewModel.RpcPort)
            or nameof(NodeViewModel.Enabled) or nameof(NodeViewModel.Name)) CommandPreview = "";
    }

    /// <summary>Читает флаг и профиль до показа окна; отключённый профиль не открывается</summary>
    public void Initialize()
    {
        try { RememberSettings = _store.LoadAutomaticSettings(); }
        catch (Exception ex) { _startupLoadFailed = true; RememberSettings = false; ReportError(ex); return; }
        if (!RememberSettings) return;
        try { ApplyProfile(_store.Load()); }
        catch (Exception ex) { _profileLoadFailed = true; ReportError(ex); }
    }

    /// <summary>Читает профиль асинхронно с той же политикой автоматической загрузки</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try { RememberSettings = _store.LoadAutomaticSettings(); }
        catch (Exception ex) { _startupLoadFailed = true; RememberSettings = false; ReportError(ex); return; }
        if (!RememberSettings) return;
        try { ApplyProfile(await _store.LoadAsync(ct)); }
        catch (Exception ex) { _profileLoadFailed = true; ReportError(ex); }
    }

    /// <summary>Запоминает переключатель и при его включении завершает сохранение перед закрытием</summary>
    public void SaveOnClose()
    {
        try
        {
            if (_startupLoadFailed) return;
            _store.SaveAutomaticSettings(enabled: RememberSettings);
            if (RememberSettings && !_profileLoadFailed) { _store.Save(CreateProfile()); SaveAgentKeys(); }
        }
        catch (Exception ex) { ReportError(ex); }
    }

    // Применяет все сохраняемые пользовательские параметры без ключей доступа
    private void ApplyProfile(RouterProfile profile)
    {
        LanguageIndex = profile.Language switch { "ru" => 1, "en" => 2, _ => 0 };
        ThemeIndex = (int)profile.Theme;
        WindowGeometry = profile.Window;
        AllowAgentLan = profile.Agent.AllowLan;
        LocalAgentListenAddress = profile.Agent.ListenAddress;
        LocalAgentPort = profile.Agent.Port;
        LocalModelPath = profile.Agent.LocalModelPath;
        UseAgentTls = profile.Agent.UseTls;
        AllowRemoteControl = profile.Agent.AllowRemoteControl;
        LocalCertificateFingerprint = profile.Agent.CertificateFingerprint;
        RoleIndex = (int)profile.Role;
        RuntimeDirectory = profile.RuntimeDirectory;
        LlamaReleaseTag = profile.LlamaReleaseTag;
        LlamaBackendIndex = (int)profile.LlamaBackend;
        GpuLayers = profile.GpuLayers;
        EngineStartupTimeoutSeconds = profile.StartupTimeoutSeconds;
        ModelPath = profile.ModelPath;
        ModelDirectory = profile.ModelDirectory;
        BindAddress = profile.BindAddress;
        ApiPort = profile.ApiPort;
        RpcPort = profile.RpcPort;
        ContextLength = profile.ContextLength;
        CpuThreads = profile.CpuThreads;
        ApplyModelSettings(profile.ModelSettings);
        _appliedWorkload = Enum.IsDefined(profile.Workload) ? profile.Workload : WorkloadMode.Custom;
        WorkloadIndex = (int)_appliedWorkload;
        AutoMonitor = profile.AutoMonitor;
        MonitorIntervalSeconds = profile.MonitorIntervalSeconds;
        Nodes.Clear();
        foreach (var node in profile.Nodes) Nodes.Add(new(node, Text));
        RememberAgentKeys = profile.RememberAgentKeys;
        RestoreAgentKeys();
        _profileLoadFailed = false;
        Status = Text["ProfileLoaded"];
    }

    // Меняет словарь и сбрасывает устаревшее представление результатов
    partial void OnLanguageIndexChanged(int value)
    {
        Text.SetLanguage(value switch { 1 => "ru", 2 => "en", _ => "system" });
        OnPropertyChanged(nameof(RuntimeDiscoveryStatus));
        OnPropertyChanged(nameof(ModelDiscoveryStatus));
        OnPropertyChanged(nameof(EngineStatus));
        OnPropertyChanged(nameof(ModelLoadStageText));
        OnPropertyChanged(nameof(ModelLoadElapsedText));
        OnPropertyChanged(nameof(OverviewModelName));
        OnPropertyChanged(nameof(EffortSupport));
        OnPropertyChanged(nameof(EffortValuesText));
        OnPropertyChanged(nameof(ModelEffortNotice));
        OnPropertyChanged(nameof(ModelActivityText));
        OnPropertyChanged(nameof(WorkloadSummary));
        OnPropertyChanged(nameof(RoleSettingsHelp));
        RefreshReadinessLabels();
        foreach (var computer in Computers) computer.RefreshLabels();
        Checks.Clear();
        Status = Text["NotChecked"];
    }

    // Сохраняет только данные профиля, ключи из редакторов узлов не сериализуются
    [RelayCommand]
    private async Task SaveAsync()
    {
        try { await _store.SaveAsync(CreateProfile()); _profileLoadFailed = false; Status = Text["ProfileSaved"]; Events.Add(Status); }
        catch (Exception ex) { ReportError(ex); }
    }

    // Добавляет пустой адрес без фиктивного статуса доступности
    [RelayCommand(CanExecute = nameof(CanEditCoordinatorSettings))]
    private void AddNode() => Nodes.Add(new(new NodeEndpoint { Name = Text["NewNode"] + " " + (Nodes.Count + 1) }, Text));

    // Удаляет выбранный узел из редактируемого профиля
    [RelayCommand(CanExecute = nameof(CanEditCoordinatorSettings))]
    private void RemoveNode(NodeViewModel? node) { if (node is not null) Nodes.Remove(node); }

    // Проверяет локальные файлы и строит предпросмотр только при отсутствии блокирующих ошибок
    [RelayCommand]
    private async Task ValidateAsync(CancellationToken ct)
    {
        IsBusy = true;
        CommandPreview = "";
        Checks.Clear();
        try
        {
            var profile = CreateProfile();
            var results = await _planner.ValidateAsync(profile, ct);
            foreach (var result in results) Checks.Add($"{(result.IsError ? Text["Error"] : Text["Info"])} · {Text[result.Code]} {result.Detail}");
            if (results.Any(result => result.IsError)) Status = Text["ValidationFailed"];
            else { CommandPreview = _planner.Build(profile).ToDisplayString(); Status = Text["PlanReady"]; }
            Events.Add(Status);
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    // Выполняет опрос текущего компьютера
    [RelayCommand]
    private async Task InspectAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var snapshot = await _probe.ReadAsync(ct);
            HardwareSummary = FormatSnapshot(snapshot);
            GpuSummary = snapshot.NvidiaQuerySucceeded ? snapshot.GpuInformation : Text["GpuUnavailable"];
            Status = Text["InspectionFinished"];
            Events.Add(Status);
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    // Запрашивает снимок через MagicOnion, отдельно от вычислительного RPC
    [RelayCommand(CanExecute = nameof(IsCoordinatorRole))]
    private async Task ProbeNodeAsync(NodeViewModel? node, CancellationToken ct)
    {
        if (node is null) return;
        node.Status = Text["Connecting"];
        try
        {
            var snapshot = await _agent.ProbeAsync(node.ToEndpoint(), node.AccessToken, ct);
            node.Status = Text["AgentConnected"] + Environment.NewLine + FormatSnapshot(snapshot) + Environment.NewLine
                + (snapshot.NvidiaQuerySucceeded ? snapshot.GpuInformation : Text["GpuUnavailable"]);
            Events.Add(node.Name + " · " + Text["AgentConnected"]);
        }
        catch (Exception ex) { node.Status = Text["ConnectionFailed"] + " · " + ex.Message; ReportError(ex); }
    }

    // Формирует профиль без токенов доступа и результатов диагностики
    private RouterProfile CreateProfile() => new()
    {
        Language = LanguageIndex switch { 1 => "ru", 2 => "en", _ => "system" },
        Theme = (AppTheme)ThemeIndex, Window = WindowGeometry, Role = (NodeRole)RoleIndex,
        Agent = new() { ListenAddress = LocalAgentListenAddress.Trim(), Port = LocalAgentPort, AllowLan = AllowAgentLan,
            UseTls = UseAgentTls, AllowRemoteControl = AllowRemoteControl, CertificateFingerprint = LocalCertificateFingerprint,
            LocalModelPath = LocalModelPath.Trim() },
        RuntimeDirectory = RuntimeDirectory, ModelPath = ModelPath, ModelDirectory = ModelDirectory, BindAddress = BindAddress, ApiPort = ApiPort,
        RpcPort = RpcPort, ContextLength = ContextLength, CpuThreads = CpuThreads, Nodes = Nodes.Select(node => node.ToEndpoint()).ToList(),
        LlamaReleaseTag = LlamaReleaseTag.Trim(), LlamaBackend = (LlamaBackend)LlamaBackendIndex,
        GpuLayers = GpuLayers, StartupTimeoutSeconds = EngineStartupTimeoutSeconds, ModelSettings = CreateModelSettings(), AutoMonitor = AutoMonitor,
        MonitorIntervalSeconds = MonitorIntervalSeconds,
        Workload = _appliedWorkload,
        RememberAgentKeys = RememberAgentKeys
    };

    // Подписывает бюджет памяти как ограничение среды .NET, а не физический объём RAM
    private string FormatSnapshot(NodeSnapshot snapshot) => $"{snapshot.HostName} · {snapshot.OperatingSystem}{Environment.NewLine}"
        + $"{Text["LogicalProcessors"]}: {snapshot.LogicalProcessors} · {Text["MemoryBudget"]}: {snapshot.ManagedMemoryBudgetBytes / 1073741824d:N1} GiB";

    // Показывает ошибку и пишет диагностическое сообщение без ключей доступа
    private void ReportError(Exception ex)
    {
        Trace.TraceError("Desktop: {0}", ex);
        Status = Text["OperationFailed"] + " · " + ex.Message;
        Events.Add(Status);
    }
}
