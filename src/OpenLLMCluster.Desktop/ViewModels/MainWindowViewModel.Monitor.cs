namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private readonly Dictionary<NodeViewModel, ComputerLoadViewModel> _remoteLoads = new();
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _monitorStarted;
    public ObservableCollection<ComputerLoadViewModel> Computers { get; } = [];
    [ObservableProperty] private bool _isMonitoringBusy;
    [ObservableProperty] private bool _autoMonitor = true;
    [ObservableProperty] private int _monitorIntervalSeconds = 3;
    public int[] MonitorIntervals { get; } = [1, 2, 3, 5, 10, 15, 30, 60];
    public Func<CancellationToken, Task<NodeSnapshot>>? LocalLoadReader { get; set; }
    public Func<NodeEndpoint, string, CancellationToken, Task<NodeSnapshot>>? RemoteLoadReader { get; set; }

    // Применяет выбранные секунды к действующему таймеру, неизвестное старое значение возвращает к исходному
    partial void OnMonitorIntervalSecondsChanged(int value)
    {
        if (!MonitorIntervals.Contains(value)) { MonitorIntervalSeconds = 3; return; }
        _monitorTimer.Interval = TimeSpan.FromSeconds(value);
    }

    /// <summary>Начинает периодический опрос после показа окна; перекрывающиеся циклы исключены</summary>
    public void StartMonitoring()
    {
        if (_monitorStarted || _disposed) return;
        _monitorStarted = true;
        _monitorTimer.Tick += MonitorTick;
        _monitorTimer.Start();
        _ = RefreshLoadsCommand.ExecuteAsync(null);
    }

    // Вызывает асинхронную команду без накопления очереди запросов
    private void MonitorTick(object? sender, EventArgs args)
    {
        if (AutoMonitor && !IsMonitoringBusy && !_disposed) _ = RefreshLoadsCommand.ExecuteAsync(null);
    }

    // Обновляет все компьютеры параллельно, отдельно обрабатывая отказ каждого агента
    [RelayCommand]
    private async Task RefreshLoadsAsync(CancellationToken ct)
    {
        if (IsMonitoringBusy || _disposed) return;
        IsMonitoringBusy = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
        try
        {
            SyncComputerCards();
            var tasks = new List<Task> { UpdateLocalLoadAsync(Computers[0], linked.Token), RefreshModelActivityAsync(linked.Token) };
            foreach (var node in Nodes.ToArray())
            {
                var card = _remoteLoads[node];
                if (!node.Enabled) { card.Unavailable("MonitorDisabled"); continue; }
                if (string.IsNullOrWhiteSpace(node.AccessToken)) { card.Unavailable("MonitorNeedsKey"); continue; }
                tasks.Add(UpdateRemoteLoadAsync(node, card, linked.Token));
            }
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Trace.TraceWarning("Мониторинг кластера: {0}", ex.GetType().Name); }
        finally { IsMonitoringBusy = false; }
    }

    // Обновляет локальную карточку тем же сборщиком, который используется агентом
    private async Task UpdateLocalLoadAsync(ComputerLoadViewModel card, CancellationToken ct)
    {
        try
        {
            var snapshot = await (LocalLoadReader?.Invoke(ct) ?? _probe.ReadAsync(ct));
            if (!_disposed) card.Apply(snapshot);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { if (!_disposed) card.Unavailable("MonitorOffline", ex.GetType().Name); }
    }

    // Не выводит ключи и не добавляет повторяющиеся ошибки опроса в общий журнал
    private async Task UpdateRemoteLoadAsync(NodeViewModel node, ComputerLoadViewModel card, CancellationToken ct)
    {
        var endpoint = node.ToEndpoint();
        var token = node.AccessToken;
        try
        {
            var snapshot = await (RemoteLoadReader?.Invoke(endpoint, token, ct) ?? _agent.ProbeAsync(endpoint, token, ct));
            if (_disposed) return;
            if (Nodes.Contains(node) && node.ToEndpoint() == endpoint && node.AccessToken == token) card.Apply(snapshot);
            else card.Unavailable("MonitorWaiting");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { if (!_disposed) card.Unavailable("MonitorOffline", ex is Grpc.Core.RpcException ? Text["MonitorCheckKey"] : ex.GetType().Name); }
    }

    // Сохраняет отдельную карточку по экземпляру редактора даже при одинаковых именах узлов
    private void SyncComputerCards()
    {
        if (Computers.Count == 0) Computers.Add(new(Text));
        Computers[0].Name = Text["LocalComputer"];
        Computers[0].Address = Environment.MachineName + " · " + Text[RoleIndex == 0 ? "CoordinatorRole" : "WorkerRole"];
        foreach (var old in _remoteLoads.Keys.Where(node => !Nodes.Contains(node)).ToArray())
        { Computers.Remove(_remoteLoads[old]); _remoteLoads.Remove(old); }
        foreach (var node in Nodes)
        {
            if (!_remoteLoads.TryGetValue(node, out var card))
            { card = new(Text); _remoteLoads[node] = card; Computers.Add(card); }
            card.Name = node.Name;
            card.Address = node.Host + ":" + node.AgentPort;
        }
    }

    // Останавливает расписание при закрытии; текущий опрос отменяется общим lifetime
    private void StopMonitoring()
    {
        _monitorTimer.Stop();
        _monitorTimer.Tick -= MonitorTick;
    }
}
