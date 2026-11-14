namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    // Создаёт идентичность до запуска агента, чтобы отпечаток можно было сверить на другом ПК
    [RelayCommand]
    private void PrepareAgentIdentity()
    {
        try { LocalCertificateFingerprint = _localAgent.CertificateStore.Fingerprint(); }
        catch (Exception ex) { ReportError(ex); }
    }

    // Сопряжение подтверждает ключ и заранее сверенный сертификат настоящим запросом
    [RelayCommand(CanExecute = nameof(IsCoordinatorRole))]
    private async Task PairNodeAsync(NodeViewModel? node, CancellationToken ct)
    {
        if (node is null || node.IsRemoteBusy) return;
        node.IsRemoteBusy = true;
        try
        {
            if (!node.UseTls || node.AccessToken.Length < 24) throw new InvalidOperationException(Text["PairingRequiresTls"]);
            var snapshot = await _agent.ProbeAsync(node.ToEndpoint(), node.AccessToken, ct);
            node.Status = Text["NodePaired"] + " · " + snapshot.HostName;
            Events.Add(node.Name + " · " + Text["NodePaired"]);
        }
        catch (Exception ex) { node.Status = Text["ConnectionFailed"] + " · " + ex.Message; ReportError(ex); }
        finally { node.IsRemoteBusy = false; }
    }

    // Запускает только управляемый RPC агентом выбранного узла
    [RelayCommand(CanExecute = nameof(IsCoordinatorRole))]
    private Task StartRemoteRpcAsync(NodeViewModel? node, CancellationToken ct) => RemoteRpcAsync(node, "start", ct);

    // Остановка не затрагивает RPC, ранее запущенный вручную или другим Desktop
    [RelayCommand(CanExecute = nameof(IsCoordinatorRole))]
    private Task StopRemoteRpcAsync(NodeViewModel? node, CancellationToken ct) => RemoteRpcAsync(node, "stop", ct);

    // Запрашивает ограниченный журнал собственного RPC агента
    [RelayCommand(CanExecute = nameof(IsCoordinatorRole))]
    private Task ReadRemoteRpcAsync(NodeViewModel? node, CancellationToken ct) => RemoteRpcAsync(node, "read", ct);

    // Отображает ошибки в карточке и исключает повторное управление тем же узлом
    private async Task RemoteRpcAsync(NodeViewModel? node, string action, CancellationToken ct)
    {
        if (node is null || node.IsRemoteBusy) return;
        node.IsRemoteBusy = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
        try
        {
            var endpoint = node.ToEndpoint();
            var result = action switch
            {
                "start" => await _agent.StartRpcAsync(endpoint, node.AccessToken,
                    new() { ListenAddress = node.RemoteRpcAddress.Trim(), Port = node.RpcPort }, linked.Token),
                "stop" => await _agent.StopRpcAsync(endpoint, node.AccessToken, linked.Token),
                _ => await _agent.ReadRpcAsync(endpoint, node.AccessToken, linked.Token)
            };
            node.RemoteLog = result.Log;
            node.Status = Text[result.IsRunning ? "RemoteRpcRunning" : "RemoteRpcStopped"] + " · " + result.ReleaseTag + " · " + result.Port;
        }
        catch (Exception ex) { node.Status = Text["OperationFailed"] + " · " + ex.Message; ReportError(ex); }
        finally { node.IsRemoteBusy = false; }
    }
}
