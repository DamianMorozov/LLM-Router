namespace OpenLLMCluster.Agent;

/// <summary>Владеет единственным RPC-процессом агента из установленной согласованной сборки</summary>
public sealed class RpcController(LlamaInstallationStore store, EngineProcessHost host) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _tag = "";
    private int _port;

    /// <summary>Запускает только управляемый RPC, последовательно с остановкой и чтением состояния</summary>
    public async Task<RemoteRpcStatus> StartAsync(RpcStartRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (host.IsRunning) throw new InvalidOperationException("RPC агента уже запущен");
            if (!IPAddress.TryParse(request.ListenAddress, out _) || request.Port is < 1 or > 65535)
                throw new ArgumentException("Недопустимый адрес или порт RPC");
            var installation = store.ReadCurrent() ?? throw new InvalidOperationException("Сначала установи сборку llama.cpp через Desktop узла");
            await host.StartAsync(new() { Role = OpenLLMCluster.Core.NodeRole.Worker, RuntimeDirectory = installation.Directory,
                LlamaReleaseTag = installation.ReleaseTag, BindAddress = request.ListenAddress, RpcPort = request.Port, StartupTimeoutSeconds = 40 }, "", ct);
            _tag = installation.ReleaseTag;
            _port = request.Port;
            return Snapshot();
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Удалённый RPC: {0}", ex.GetType().Name); throw; }
        finally { _gate.Release(); }
    }

    /// <summary>Читает текущий журнал, не возвращая произвольные файлы с узла</summary>
    public async Task<RemoteRpcStatus> ReadAsync(bool stop = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { if (stop) host.Stop(); return Snapshot(); }
        finally { _gate.Release(); }
    }

    // Возвращает только последние строки собственного вычислительного процесса
    private RemoteRpcStatus Snapshot() => new() { IsRunning = host.IsRunning, ReleaseTag = _tag, Port = _port, Log = host.DiagnosticOutput };

    /// <summary>Завершает собственный процесс при остановке агента</summary>
    public void Dispose() { host.Dispose(); _gate.Dispose(); }
}

/// <summary>Настройки доступа к отдельному контракту удалённого управления</summary>
public sealed record NodeControlPolicy(bool Enabled);

/// <summary>Разрешает ограниченное управление только при явном включении, TLS и сильном ключе</summary>
public sealed class NodeControlService(RpcController controller, NodeControlPolicy policy, LocalModelManager models) : ServiceBase<INodeControl>, INodeControl
{
    /// <summary>Запускает вычислительный RPC с указанным адресом прослушивания</summary>
    public async UnaryResult<RemoteRpcStatus> StartRpcAsync(RpcStartRequest request)
    {
        RequirePermission();
        try { return await controller.StartAsync(request, Context.CallContext.CancellationToken); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException)
        { throw new Grpc.Core.RpcException(new(Grpc.Core.StatusCode.FailedPrecondition, ex.Message)); }
    }

    /// <summary>Останавливает RPC, принадлежащий агенту</summary>
    public async UnaryResult<RemoteRpcStatus> StopRpcAsync()
    { RequirePermission(); return await controller.ReadAsync(stop: true, Context.CallContext.CancellationToken); }

    /// <summary>Возвращает журнал и состояние собственного RPC</summary>
    public async UnaryResult<RemoteRpcStatus> GetRpcStatusAsync()
    { RequirePermission(); return await controller.ReadAsync(ct: Context.CallContext.CancellationToken); }

    /// <summary>Использует только путь, выбранный на самом узле; несовпадение весов блокирует подготовку</summary>
    public async UnaryResult<LocalModelStatus> PrepareLocalModelAsync(LocalModelIdentity expected)
    {
        RequirePermission();
        try { return await models.PrepareAsync(expected, Context.CallContext.CancellationToken); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or System.IO.InvalidDataException
            or UnauthorizedAccessException)
        { throw new Grpc.Core.RpcException(new(Grpc.Core.StatusCode.FailedPrecondition, ex.Message)); }
    }

    // Не разрешает управление по старому диагностическому HTTP-каналу
    private void RequirePermission()
    {
        if (!policy.Enabled) throw new Grpc.Core.RpcException(new(Grpc.Core.StatusCode.PermissionDenied, "Удалённое управление отключено на узле"));
    }
}
