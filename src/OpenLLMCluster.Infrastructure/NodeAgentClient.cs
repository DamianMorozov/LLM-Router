namespace OpenLLMCluster.Infrastructure;

/// <summary>Подключается к агенту через MagicOnion; проверяет TLS-идентичность и передаёт ключ в заголовке</summary>
public sealed class NodeAgentClient
{
    /// <summary>Проверяет доступность, авторизацию и версию протокола узла</summary>
    public async Task<NodeSnapshot> ProbeAsync(NodeEndpoint node, string accessToken, CancellationToken ct = default)
    {
        try
        {
            if (!RuntimePlanner.IsValidNode(node)) throw new ArgumentException("Недопустимый адрес узла");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var channel = CreateChannel(node);
            var headers = new Metadata();
            if (!string.IsNullOrEmpty(accessToken)) headers.Add("x-router-token", accessToken);
            var client = MagicOnionClient.Create<INodeAgent>(channel).WithHeaders(headers).WithCancellationToken(timeout.Token);
            var snapshot = await client.GetStatusAsync();
            if (snapshot.ProtocolVersion != 1) throw new InvalidDataException("Несовместимая версия протокола агента");
            return snapshot;
        }
        catch (Exception ex) { Trace.TraceError("Probe node {0}: {1}", node.Name, ex.GetType().Name); throw; }
    }

    /// <summary>Запускает RPC через закреплённый TLS-канал</summary>
    public Task<RemoteRpcStatus> StartRpcAsync(NodeEndpoint node, string token, RpcStartRequest request, CancellationToken ct = default)
        => ControlAsync(node, token, client => client.StartRpcAsync(request).ResponseAsync, 55, ct);

    /// <summary>Останавливает только процесс RPC, созданный агентом</summary>
    public Task<RemoteRpcStatus> StopRpcAsync(NodeEndpoint node, string token, CancellationToken ct = default)
        => ControlAsync(node, token, client => client.StopRpcAsync().ResponseAsync, 55, ct);

    /// <summary>Запрашивает состояние и журнал удалённого RPC</summary>
    public Task<RemoteRpcStatus> ReadRpcAsync(NodeEndpoint node, string token, CancellationToken ct = default)
        => ControlAsync(node, token, client => client.GetRpcStatusAsync().ResponseAsync, 55, ct);

    /// <summary>Передаёт только размеры и SHA-256; чтение GGUF и подготовка кэша выполняются на узле</summary>
    public Task<LocalModelStatus> PrepareLocalModelAsync(NodeEndpoint node, string token, LocalModelIdentity expected, CancellationToken ct = default)
        => ControlAsync(node, token, client => client.PrepareLocalModelAsync(expected).ResponseAsync, 1800, ct);

    // Ограничивает время управления и использует тот же способ авторизации, что и диагностика
    private static async Task<T> ControlAsync<T>(NodeEndpoint node, string token,
        Func<INodeControl, Task<T>> operation, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            if (!node.UseTls || token.Length < 24) throw new InvalidOperationException("Для управления нужны TLS и ключ от 24 символов");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var channel = CreateChannel(node);
            var client = MagicOnionClient.Create<INodeControl>(channel).WithHeaders(new Metadata { { "x-router-token", token } })
                .WithCancellationToken(timeout.Token);
            return await operation(client);
        }
        catch (Exception ex) { Trace.TraceError("Управление RPC: {0}", ex.GetType().Name); throw; }
    }

    // Проверяет сертификат только по явно закреплённому SHA-256; неверный отпечаток не принимается
    private static GrpcChannel CreateChannel(NodeEndpoint node)
    {
        if (!RuntimePlanner.IsValidNode(node)) throw new ArgumentException("Недопустимый адрес узла");
        if (node.UseTls && (node.CertificateFingerprint.Length == 0)) throw new ArgumentException("Введи SHA-256 сертификата узла");
        var handler = new HttpClientHandler();
        if (node.UseTls) handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            AgentCertificateStore.Matches(certificate, node.CertificateFingerprint);
        return GrpcChannel.ForAddress(new UriBuilder(node.UseTls ? "https" : "http", node.Host, node.AgentPort).Uri,
            new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }
}
