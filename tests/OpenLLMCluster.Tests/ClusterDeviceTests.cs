// Проверяет порядок реальных устройств, резерв VRAM, явный split и разделение серверных timing от HTTP-времени
namespace OpenLLMCluster.Tests;

public sealed class ClusterDeviceTests
{
    [Fact]
    public void DeviceParserAndSuggestionPreserveActualRpcCudaOrder()
    {
        var devices = ClusterDevicePlanner.Parse("""
            load_backend: ignored diagnostic
            Available devices:
              RPC0: worker.example:50052 (16384 MiB, 12000 MiB free)
              CUDA0: Test GPU (8192 MiB, 4000 MiB free)
              CUDA1: Busy GPU (8192 MiB, 256 MiB free)
            """);
        Assert.Equal(new[] { "RPC0", "CUDA0", "CUDA1" }, devices.Select(device => device.Id));
        var split = ClusterDevicePlanner.Suggest(devices, reserveMiB: 1024);
        Assert.Equal("RPC0,CUDA0", split.Devices);
        Assert.Equal("10976,2976", split.TensorSplit);
        var settings = new ModelSettings { Devices = split.Devices, TensorSplit = split.TensorSplit };
        Assert.True(settings.IsValid());
        var args = new RuntimePlanner().Build(new() { GpuLayers = 99, ModelSettings = settings }).Arguments;
        Assert.Equal(split.Devices, args[Array.IndexOf(args, "--device") + 1]);
        Assert.Equal(split.TensorSplit, args[Array.IndexOf(args, "--tensor-split") + 1]);
        Assert.False((settings with { Devices = "CUDA0" }).IsValid());
        Assert.False((settings with { TensorSplit = "0,0" }).IsValid());
        Assert.False((settings with { TensorSplit = "1,-1" }).IsValid());
        Assert.False((settings with { TensorSplit = "NaN,1" }).IsValid());
        Assert.Throws<InvalidOperationException>(() => ClusterDevicePlanner.Suggest(devices, reserveMiB: 50000));
    }

    [Fact]
    public async Task ApiUsesReportedPromptGenerationAndCachedTokens()
    {
        using var client = new HttpClient(new TimingHandler());
        var result = await new EngineApiClient(client).TestAsync(new("http://localhost/"), "test-key", "READY", TestContext.Current.CancellationToken);
        Assert.Equal("READY", result.Text);
        Assert.Equal(16, result.PromptTokens);
        Assert.Equal(9, result.CachedTokens);
        Assert.Equal(2, result.CompletionTokens);
        Assert.Equal(278.5, result.PromptMilliseconds);
        Assert.Equal(113.6, result.GenerationMilliseconds);
        Assert.Equal(25.1, result.PromptTokensPerSecond);
        Assert.Equal(17.6, result.GenerationTokensPerSecond);
    }

    [AvaloniaFact]
    public void SplitEditorsAndProtectedSettingsHaveWorkingBindings()
    {
        var root = Path.Combine(Path.GetTempPath(), "open-llm-cluster-cluster-ui-" + Guid.NewGuid().ToString("N"));
        var store = new ProfileStore(Path.Combine(root, "profile.json"));
        using var vm = new MainWindowViewModel(store);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            window.FindControl<ToggleSwitch>("RememberAgentKeysToggle")!.IsChecked = false;
            Assert.False(vm.RememberAgentKeys);
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("RuntimeTab");
            Dispatcher.UIThread.RunJobs();
            window.FindControl<TextBox>("DevicesEditor")!.Text = "RPC0,CUDA0";
            window.FindControl<TextBox>("TensorSplitEditor")!.Text = "2,1";
            Assert.Equal("RPC0,CUDA0", vm.Devices);
            Assert.Equal("2,1", vm.TensorSplit);
            vm.SaveOnClose();
            using var reopened = new MainWindowViewModel(store);
            reopened.Initialize();
            Assert.Equal("RPC0,CUDA0", reopened.Devices);
            Assert.Equal("2,1", reopened.TensorSplit);
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("LocalAgentTab");
            Dispatcher.UIThread.RunJobs();
            window.FindControl<ToggleSwitch>("AgentTlsSwitch")!.IsChecked = true;
            window.FindControl<ToggleSwitch>("RemoteControlSwitch")!.IsChecked = true;
            Assert.True(vm.UseAgentTls);
            Assert.True(vm.AllowRemoteControl);
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class TimingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"choices":[{"message":{"content":"READY"}}],"usage":{"prompt_tokens":16,"completion_tokens":2,
                "prompt_tokens_details":{"cached_tokens":9}},"timings":{"prompt_ms":278.5,"predicted_ms":113.6,
                "prompt_per_second":25.1,"predicted_per_second":17.6}}
                """) });
    }
}
