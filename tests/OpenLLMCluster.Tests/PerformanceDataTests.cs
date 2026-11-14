// Проверяет физическую RAM, совместимость снимков, реальные параметры профилей и диагностику активности без текста запросов
namespace OpenLLMCluster.Tests;

public sealed class PerformanceDataTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(250, 75)]
    [InlineData(1000, 0)]
    public void LinuxUsesAvailableMemoryIncludingReclaimableCache(int available, int usedPercent)
    {
        var sample = MemoryLoadProbe.ParseLinux($"MemTotal: 1000 kB\nMemFree: 1 kB\nMemAvailable: {available} kB\n");
        Assert.NotNull(sample);
        var card = new ComputerLoadViewModel(new Localizer());
        card.Apply(new() { MemoryTotalBytes = sample.TotalBytes, MemoryAvailableBytes = sample.AvailableBytes });
        Assert.True(card.HasMemory);
        Assert.Equal(usedPercent, card.MemoryUsage);
        card.Unavailable("MonitorWaiting");
        Assert.False(card.HasMemory);
        Assert.Null(card.MemoryAvailableBytes);
        Assert.Equal(card.Text["MeasurementUnavailable"], card.MemoryLabel);
    }

    [Theory]
    [InlineData("MemTotal: 1000 kB\nMemFree: 100 kB")]
    [InlineData("MemTotal: 1000 kB\nMemAvailable: 1001 kB")]
    [InlineData("MemTotal: 1000 kB\nMemAvailable: -1 kB")]
    public void MissingOrInvalidLinuxMemoryIsUnknown(string text) => Assert.Null(MemoryLoadProbe.ParseLinux(text));

    [Fact]
    public void MacMemoryUsesReportedPageSizeAndReclaimablePages()
    {
        var sample = MemoryLoadProbe.ParseMac("65536", "Mach Virtual Memory Statistics: (page size of 4096 bytes)\n"
            + "Pages free: 1.\nPages inactive: 2.\nPages speculative: 3.\n");
        Assert.Equal(new PhysicalMemorySample(65536, 24576), sample);
    }

    [Fact]
    public void ImpossibleMacMemoryDoesNotOverflowIntoAValidSample()
        => Assert.Null(MemoryLoadProbe.ParseMac("65536", "Mach Virtual Memory Statistics: (page size of 4096 bytes)\n"
            + $"Pages free: {long.MaxValue}.\nPages inactive: 2.\nPages speculative: 3.\n"));

    [Fact]
    public void OlderMessagePackSnapshotLeavesMemoryUnknownAndNewFieldsRoundTrip()
    {
        var legacy = MessagePack.MessagePackSerializer.Serialize<object?[]>(
            ["legacy", "OS", 8, 1000L, "", false, "old", 1, "", "", false, null, new object[0], default(DateTimeOffset), false],
            cancellationToken: TestContext.Current.CancellationToken);
        var restored = MessagePack.MessagePackSerializer.Deserialize<NodeSnapshot>(legacy, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(restored.MemoryTotalBytes);
        Assert.Null(restored.MemoryAvailableBytes);
        var card = new ComputerLoadViewModel(new Localizer());
        card.Apply(restored);
        Assert.False(card.HasMemory);
        var sample = new NodeSnapshot { MemoryTotalBytes = 64000, MemoryAvailableBytes = 0 };
        var bytes = MessagePack.MessagePackSerializer.Serialize(sample, cancellationToken: TestContext.Current.CancellationToken);
        restored = MessagePack.MessagePackSerializer.Deserialize<NodeSnapshot>(bytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(64000, restored.MemoryTotalBytes);
        Assert.Equal(0, restored.MemoryAvailableBytes);
    }

    [Fact]
    public async Task OperatingSystemReportsPhysicalMemoryAndPreservesCancellation()
    {
        var probe = new MemoryLoadProbe();
        var sample = await probe.ReadAsync(TestContext.Current.CancellationToken);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Assert.NotNull(sample);
            Assert.True(sample.TotalBytes > 0);
            Assert.InRange(sample.AvailableBytes, 0, sample.TotalBytes);
        }
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ReadAsync(cancel.Token));
    }

    [Theory]
    [InlineData(WorkloadMode.Interactive, 8192, 1, 512, 128)]
    [InlineData(WorkloadMode.Balanced, 16384, 1, 512, 128)]
    [InlineData(WorkloadMode.Throughput, 16384, 2, 1024, 256)]
    [InlineData(WorkloadMode.MaximumContext, 131072, 1, 512, 128)]
    public void WorkloadBecomesCliWithoutChangingModelDevicesOrReasoning(WorkloadMode mode, int context, int parallel, int batch, int microbatch)
    {
        var original = new RouterProfile { ModelPath = "models/demo.gguf", GpuLayers = 20, ApiPort = 8083,
            ModelSettings = new() { Devices = "CUDA0,RPC0", TensorSplit = "1,2", ReasoningEffort = "low", MaxOutputTokens = 2048 } };
        var profile = WorkloadProfiles.Apply(original, mode, 32);
        var args = new RuntimePlanner().Build(profile).Arguments;
        foreach (var (key, value) in new[] { ("--ctx-size", context), ("--parallel", parallel), ("--batch-size", batch), ("--ubatch-size", microbatch) })
            Assert.Equal(value.ToString(), args[Array.IndexOf(args, key) + 1]);
        Assert.Equal(original.ModelSettings.CacheTypeV, args[Array.IndexOf(args, "--cache-type-v") + 1]);
        Assert.Equal("default", args[Array.IndexOf(args, "--reasoning-effort") + 1]);
        Assert.Equal(8, profile.CpuThreads);
        Assert.Equal(original.ModelPath, profile.ModelPath);
        Assert.Equal(original.GpuLayers, profile.GpuLayers);
        Assert.Equal(original.ModelSettings.Devices, profile.ModelSettings.Devices);
        Assert.Equal(original.ModelSettings.TensorSplit, profile.ModelSettings.TensorSplit);
        var output = mode == WorkloadMode.MaximumContext ? 32768 : original.ModelSettings.MaxOutputTokens;
        Assert.Equal(output, profile.ModelSettings.MaxOutputTokens);
        Assert.Equal(output.ToString(), args[Array.IndexOf(args, "--predict") + 1]);
        Assert.Equal(original.ApiPort, profile.ApiPort);
        Assert.Equal("f16", original.ModelSettings.CacheTypeV);
    }

    [Theory]
    [InlineData(WorkloadMode.Interactive, "f16", "f16", false)]
    [InlineData(WorkloadMode.Balanced, "f16", "f16", true)]
    [InlineData(WorkloadMode.Throughput, "f16", "f16", false)]
    [InlineData(WorkloadMode.MaximumContext, "f16", "f16", true)]
    [InlineData(WorkloadMode.MaximumContext, "q8_0", "q8_0", true)]
    public void WorkloadPreservesExplicitCachePrecisionAndFlashAttention(WorkloadMode mode, string keyType, string valueType, bool flash)
    {
        var original = new RouterProfile { ModelSettings = new() { CacheTypeK = keyType, CacheTypeV = valueType, FlashAttention = flash } };
        var applied = WorkloadProfiles.Apply(original, mode, 16);
        var args = new RuntimePlanner().Build(applied).Arguments;
        Assert.Equal(keyType, args[Array.IndexOf(args, "--cache-type-k") + 1]);
        Assert.Equal(valueType, args[Array.IndexOf(args, "--cache-type-v") + 1]);
        Assert.Equal(flash ? "on" : "off", args[Array.IndexOf(args, "--flash-attn") + 1]);
    }

    [AvaloniaFact]
    public void OnlyAppliedWorkloadPersistsAndManualChangesReturnToCustom()
    {
        var directory = Path.Combine(Path.GetTempPath(), "olc-workload-" + Guid.NewGuid().ToString("N"));
        var store = new ProfileStore(Path.Combine(directory, "profile.json"));
        using var vm = new MainWindowViewModel(store) { WorkloadIndex = 1, RememberAgentKeys = false };
        try
        {
            vm.ApplyWorkloadProfileCommand.Execute(null);
            vm.SaveOnClose();
            using var restored = new MainWindowViewModel(store);
            restored.Initialize();
            Assert.Equal(1, restored.WorkloadIndex);
            Assert.Equal(8192, restored.ContextLength);
            Assert.Equal("f16", restored.CacheTypeV);
            restored.WorkloadIndex = 3;
            restored.SaveOnClose();
            Assert.Equal(WorkloadMode.Interactive, store.Load().Workload);
            restored.ContextLength = 32768;
            Assert.Equal(0, restored.WorkloadIndex);
            restored.SaveOnClose();
            Assert.Equal(WorkloadMode.Custom, store.Load().Workload);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("[]", ModelActivityKind.Idle, 0, 0)]
    [InlineData("[{\"is_processing\":true,\"n_prompt_tokens\":94545,\"next_token\":[{\"n_decoded\":1721}]}]",
        ModelActivityKind.Generating, 94545, 1721)]
    [InlineData("[{\"is_processing\":true,\"n_prompt_tokens\":128,\"next_token\":{\"n_decoded\":0}}]", ModelActivityKind.Prompt, 128, 0)]
    [InlineData("[{\"is_processing\":false,\"n_prompt_tokens\":100,\"next_token\":{\"n_decoded\":25}}]", ModelActivityKind.Idle, 0, 0)]
    [InlineData("{}", ModelActivityKind.Unavailable, 0, 0)]
    public void ActivityUsesObservedSlotCounters(string json, ModelActivityKind expected, int prompt, int decoded)
    {
        var result = EngineApiClient.ParseActivity(json);
        Assert.Equal(expected, result.Kind);
        Assert.Equal(prompt, result.PromptTokens);
        Assert.Equal(decoded, result.DecodedTokens);
        Assert.Equal(expected is ModelActivityKind.Generating or ModelActivityKind.Prompt ? 1 : 0, result.ActiveSlots);
    }

    [Fact]
    public void NonNumericActivityCountersAreIgnored()
    {
        var result = EngineApiClient.ParseActivity("""[{"is_processing":true,"n_prompt_tokens":"unknown","next_token":[{"n_decoded":null}]}]""");
        Assert.Equal(ModelActivityKind.Prompt, result.Kind);
        Assert.Equal(0, result.PromptTokens);
    }

    [Theory]
    [InlineData(200, ModelActivityKind.Idle)]
    [InlineData(404, ModelActivityKind.Unavailable)]
    [InlineData(401, ModelActivityKind.Unavailable)]
    public async Task ActivityAuthenticatesAndHandlesUnsupportedOrRejectedRequests(int status, ModelActivityKind expected)
    {
        using var client = new HttpClient(new ActivityHandler(request =>
        {
            Assert.Equal("/slots", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-only-key", request.Headers.Authorization.Parameter);
            return new((HttpStatusCode)status) { Content = new StringContent("[]") };
        }));
        var result = await new EngineApiClient(client).ReadActivityAsync(new("http://localhost/"), "test-only-key", TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Kind);
    }

    private sealed class ActivityHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
