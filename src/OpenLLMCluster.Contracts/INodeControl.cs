namespace OpenLLMCluster.Contracts;

/// <summary>Ограниченное управление собственным RPC агента; произвольные команды и пути не принимаются</summary>
public interface INodeControl : IService<INodeControl>
{
    /// <summary>Запускает RPC из управляемой локальной установки</summary>
    UnaryResult<RemoteRpcStatus> StartRpcAsync(RpcStartRequest request);
    /// <summary>Останавливает только принадлежащий агенту RPC</summary>
    UnaryResult<RemoteRpcStatus> StopRpcAsync();
    /// <summary>Читает состояние и ограниченный журнал RPC</summary>
    UnaryResult<RemoteRpcStatus> GetRpcStatusAsync();
    /// <summary>Сверяет локально выбранный GGUF и подготавливает кэш без передачи весов по сети</summary>
    UnaryResult<LocalModelStatus> PrepareLocalModelAsync(LocalModelIdentity expected);
}

/// <summary>Сетевые параметры вычислительного процесса, без исполняемых команд</summary>
[MessagePackObject]
public sealed class RpcStartRequest
{
    [Key(0)] public string ListenAddress { get; set; } = "0.0.0.0";
    [Key(1)] public int Port { get; set; } = 50052;
}

/// <summary>Состояние процесса, которым владеет агент</summary>
[MessagePackObject]
public sealed class RemoteRpcStatus
{
    [Key(0)] public bool IsRunning { get; set; }
    [Key(1)] public string ReleaseTag { get; set; } = "";
    [Key(2)] public string Log { get; set; } = "";
    [Key(3)] public int Port { get; set; }
}
