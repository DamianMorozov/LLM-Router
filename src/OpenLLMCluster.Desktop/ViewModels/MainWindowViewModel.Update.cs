namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUseWorkspace), nameof(CanViewNodes), nameof(CanEditEngine), nameof(CanEditLocalAgent))]
    [NotifyCanExecuteChangedFor(nameof(StartEngineCommand), nameof(InstallLlamaCommand), nameof(StartLocalAgentCommand))]
    private bool _isPreparingUpdate;
    public bool CanUseWorkspace => !IsPreparingUpdate;
    public bool CanViewNodes => CanUseWorkspace && IsCoordinatorRole;
    public Action? CaptureWindowSettings { get; set; }

    // Удерживает блокировку до выхода обновлятора; при ошибке возвращает управление пользователю
    private async Task InstallUpdateAsync(ApplicationUpdateService service, CancellationToken ct)
    {
        IsPreparingUpdate = true;
        try { await service.ApplyAsync(PrepareUpdateAsync, ct); }
        catch
        {
            IsPreparingUpdate = false;
            if (_monitorStarted) _monitorTimer.Start();
            throw;
        }
    }

    // Прекращает собственные операции и процессы до сохранения профиля и передачи управления Velopack
    private async Task PrepareUpdateAsync(CancellationToken ct)
    {
        _monitorTimer.Stop();
        var ports = new List<(string Host, int Port)>();
        if (_engine.IsRunning || IsEngineStarting)
            ports.Add((_engine.BaseUri?.Host ?? NetworkPortProbe.LocalProbeAddress(BindAddress),
                (_runningRole ?? (NodeRole)RoleIndex) == NodeRole.Worker ? RpcPort : ApiPort));
        if (_localAgent.IsRunning || StartLocalAgentCommand.IsRunning)
            ports.Add((NetworkPortProbe.LocalProbeAddress(LocalAgentListenAddress), LocalAgentPort));
        var commands = GetType().GetProperties().Where(property => typeof(IAsyncRelayCommand).IsAssignableFrom(property.PropertyType))
            .Select(property => (IAsyncRelayCommand)property.GetValue(this)!).Where(command => command.IsRunning).ToArray();
        var tasks = commands.Select(command => command.ExecutionTask).OfType<Task>().ToArray();
        foreach (var command in commands) command.Cancel();
        _engineOperation?.Cancel();
        // Остановка движка сначала закрывает inference-шлюз, затем завершает llama-server или локальный RPC
        _engine.Stop();
        _localAgent.Stop();
        foreach (var task in tasks) await WaitForUpdateOperationAsync(task, ct);
        if (_engine.IsRunning || _localAgent.IsRunning) throw new InvalidOperationException("Не все собственные процессы остановлены");
        foreach (var (host, port) in ports)
            if ((await _portProbe.CheckAsync(host, port, ct)).IsOpen)
                throw new InvalidOperationException($"Порт {host}:{port} ещё занят; обновление отменено");
        IsEngineRunning = false;
        IsEngineStarting = false;
        IsLocalAgentRunning = false;
        CompleteModelLoading("ModelNotLoaded");
        SetEngineStatus("EngineStopped");
        LocalAgentStatus = Text["LocalAgentStopped"];
        ct.ThrowIfCancellationRequested();
        CaptureWindowSettings?.Invoke();
        SaveForUpdate();
    }

    // Отмена выполнявшейся команды допустима; таймаут или другая ошибка блокируют обновление
    private static async Task WaitForUpdateOperationAsync(Task task, CancellationToken ct)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && task.IsCanceled) { }
    }

    // В отличие от обычного закрытия не скрывает ошибку сохранения от координатора установки
    private void SaveForUpdate()
    {
        try
        {
            if (_startupLoadFailed || (RememberSettings && _profileLoadFailed))
                throw new InvalidOperationException("Не удалось прочитать исходные настройки; перед обновлением исправьте профиль");
            _store.SaveAutomaticSettings(enabled: RememberSettings);
            if (RememberSettings) { _store.Save(CreateProfile()); SaveAgentKeys(); }
        }
        catch (Exception ex) { Trace.TraceError("Save settings before update: {0}", ex); throw; }
    }
}
