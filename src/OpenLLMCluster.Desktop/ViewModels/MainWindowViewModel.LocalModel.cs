namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private string _localModelPath = "";
    [ObservableProperty] private string _localModelStatus = "";
    [ObservableProperty] private bool _isLocalModelBusy;
    public bool CanConfigureLocalModel => CanEditLocalAgent && CanEditEngine && IsWorkerRole;

    // Подготавливает кэш локально до запуска агента; удалённая проверка дополнительно сверит его с главным ПК
    [RelayCommand(CanExecute = nameof(CanConfigureLocalModel), IncludeCancelCommand = true)]
    private async Task PrepareLocalModelAsync(CancellationToken ct)
    {
        IsLocalModelBusy = true;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
            if (LlamaReleaseTag != LlamaBuildDefaults.ReleaseTag)
                throw new InvalidOperationException(Text["LocalModelBuildRequired"] + " " + LlamaBuildDefaults.ReleaseTag);
            var progress = new Progress<string>(key => LocalModelStatus = Text[key]);
            var result = await Task.Run(() => new RpcModelCache().PrepareAsync(LocalModelPath.Trim(), RuntimeDirectory,
                progress: progress, ct: linked.Token), linked.Token);
            LocalModelStatus = FormatLocalModel(result);
            Events.Add(LocalModelStatus);
        }
        catch (OperationCanceledException) { LocalModelStatus = Text["AgentActionCancelled"]; }
        catch (Exception ex) { LocalModelStatus = Text["OperationFailed"] + " · " + ex.Message; ReportError(ex); }
        finally { IsLocalModelBusy = false; }
    }

    // Отображает результат именно того узла, на котором выполнялась проверка
    private async Task CheckNodeLocalModelAsync(NodeViewModel node, LocalModelIdentity identity, CancellationToken ct)
    {
        node.Status = Text["LocalModelPreparing"];
        try
        {
            var prepared = await _agent.PrepareLocalModelAsync(node.ToEndpoint(), node.AccessToken, identity, ct);
            if (!prepared.Configured) throw new InvalidOperationException(Text["LocalModelNotConfigured"] + " · " + node.Name);
            RpcModelCache.RequireMatch(identity, prepared.Identity);
            node.Status = FormatLocalModel(prepared);
            Events.Add(node.Name + " · " + node.Status);
        }
        catch (Exception ex)
        {
            node.Status = Text[ex is OperationCanceledException ? "AgentActionCancelled" : "OperationFailed"] + " · " + ex.Message;
            Trace.TraceError("Проверка локальной модели узла: {0}", ex.GetType().Name);
            throw;
        }
    }

    // Показывает отпечатки и объём кэша без содержимого весов
    private string FormatLocalModel(LocalModelStatus result) => Text["LocalModelReady"]
        + $" · {result.CachedTensors} · {result.CachedBytes / 1073741824d:F2} GiB"
        + Environment.NewLine + string.Join(Environment.NewLine, result.Identity.Files.Select((file, index) =>
            $"{index + 1}: {file.Length} B · SHA-256 {file.Sha256}"));
}
