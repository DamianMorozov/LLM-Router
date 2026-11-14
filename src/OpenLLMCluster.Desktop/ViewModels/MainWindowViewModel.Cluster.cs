namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private string _clusterDevicesReport = "";

    // Сверяет build всех включённых узлов и показывает реальный порядок устройств llama.cpp
    [RelayCommand(CanExecute = nameof(CanEditCoordinatorSettings))]
    private Task CheckClusterAsync(CancellationToken ct) => DiscoverClusterAsync(applySplit: false, ct);

    // Заново опрашивает устройства перед применением пропорций по свободной памяти
    [RelayCommand(CanExecute = nameof(CanEditCoordinatorSettings))]
    private Task SuggestGpuSplitAsync(CancellationToken ct) => DiscoverClusterAsync(applySplit: true, ct);

    // Не выдаёт телеметрию агента за проверку вычислительного RPC-протокола
    private async Task DiscoverClusterAsync(bool applySplit, CancellationToken ct)
    {
        if (!IsCoordinatorRole) return;
        IsEngineBusy = true;
        ClusterDevicesReport = "";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
        try
        {
            var profile = CreateProfile();
            foreach (var node in Nodes.Where(node => node.Enabled))
            {
                var snapshot = await _agent.ProbeAsync(node.ToEndpoint(), node.AccessToken, linked.Token);
                ClusterBuildGuard.RequireCompatible(profile.LlamaReleaseTag, LlamaInstallationStore.ReadDirectory(profile.RuntimeDirectory), snapshot, node.Name);
            }
            var devices = await new ClusterDevicePlanner().DiscoverAsync(profile, linked.Token);
            ClusterDevicesReport = devices.Count == 0 ? Text["NoClusterDevices"]
                : string.Join(Environment.NewLine, devices.Select(device =>
                    $"{device.Id} · {device.Description} · {device.FreeMiB} / {device.TotalMiB} MiB"));
            if (applySplit)
            {
                var suggestion = ClusterDevicePlanner.Suggest(devices, DeviceReserveMiB);
                Devices = suggestion.Devices;
                TensorSplit = suggestion.TensorSplit;
            }
            Events.Add(Text["ClusterChecked"] + Environment.NewLine + ClusterDevicesReport);
        }
        catch (Exception ex) { ClusterDevicesReport = Text["OperationFailed"] + " · " + ex.Message; ReportError(ex); }
        finally { IsEngineBusy = false; }
    }

    // Измеряет два одинаковых запроса без принудительной очистки KV-cache работающего сервера
    [RelayCommand(CanExecute = nameof(CanTestEngine))]
    private async Task BenchmarkEngineAsync(CancellationToken ct)
    {
        IsEngineBusy = true;
        _engineOperation = CancellationTokenSource.CreateLinkedTokenSource(ct, _agentLifetime.Token);
        try
        {
            var first = await _engine.TestAsync(EngineTestPrompt, _engineOperation.Token);
            EngineTestOutput = Text["FirstRequest"] + Environment.NewLine + FormatMeasurement(first);
            var second = await _engine.TestAsync(EngineTestPrompt, _engineOperation.Token);
            EngineTestOutput += Environment.NewLine + Environment.NewLine + Text["RepeatedRequest"] + Environment.NewLine + FormatMeasurement(second);
            SetEngineStatus("InferenceTestPassed");
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsEngineBusy = false; _engineOperation.Dispose(); _engineOperation = null; }
    }

    // Показывает отдельно серверные prompt/generation и полное время HTTP; неизвестные поля остаются неизвестными
    private string FormatMeasurement(EngineTestResult result) => result.Text + Environment.NewLine
        + $"{Text["PromptTokens"]}: {result.PromptTokens?.ToString() ?? "—"} · {Text["CachedTokens"]}: {result.CachedTokens?.ToString() ?? "—"}"
        + $" · {Text["OutputTokens"]}: {result.CompletionTokens} · HTTP: {result.ElapsedMilliseconds} ms{Environment.NewLine}"
        + $"Prompt: {Metric(result.PromptMilliseconds)} ms · {Metric(result.PromptTokensPerSecond)} t/s{Environment.NewLine}"
        + $"Generation: {Metric(result.GenerationMilliseconds)} ms · {Metric(result.GenerationTokensPerSecond)} t/s";

    // Сохраняет точность измерений без подстановки вычисленной скорости вместо серверных timing
    private static string Metric(double? value) => value?.ToString("F1") ?? "—";
}
