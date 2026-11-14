namespace OpenLLMCluster.Agent;

/// <summary>Возвращает живой снимок окружения; методы исполнения команд отсутствуют</summary>
public sealed class NodeAgentService(EnvironmentProbe probe, LocalModelPolicy policy) : ServiceBase<INodeAgent>, INodeAgent
{
    /// <summary>Выполняет диагностику на стороне узла</summary>
    public async UnaryResult<NodeSnapshot> GetStatusAsync()
    {
        var snapshot = await probe.ReadAsync(Context.CallContext.CancellationToken);
        snapshot.LocalModelConfigured = !string.IsNullOrWhiteSpace(policy.Path);
        return snapshot;
    }
}
