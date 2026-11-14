// Проверяет фактические аргументы запуска, культуру чисел, сохранение настроек и поддержку Effort по API
namespace OpenLLMCluster.Tests;

public sealed class ModelSettingsTests
{
    [Fact]
    public void ModelSettingsBecomeRealCliArgumentsUsingInvariantDecimals()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            var profile = new RouterProfile { ModelSettings = new() { MaxOutputTokens = 2048, Temperature = 0.35, TopP = 0.8, TopK = 23,
                MinP = 0.02, RepeatPenalty = 1.1, ReasoningMode = "on", ReasoningEffort = "low", ReasoningBudget = 512,
                Parallel = 2, BatchSize = 256, MicroBatchSize = 64, CacheTypeK = "q8_0", CacheTypeV = "q8_0" } };
            var args = new RuntimePlanner().Build(profile).Arguments;
            foreach (var pair in new Dictionary<string, string>
            {
                ["--predict"] = "2048", ["--temp"] = "0.35", ["--top-p"] = "0.8", ["--top-k"] = "23", ["--min-p"] = "0.02",
                ["--repeat-penalty"] = "1.1", ["--reasoning"] = "on", ["--reasoning-effort"] = "default", ["--reasoning-budget"] = "512",
                ["--parallel"] = "2", ["--batch-size"] = "256", ["--ubatch-size"] = "64", ["--cache-type-k"] = "q8_0", ["--cache-type-v"] = "q8_0"
            }) Assert.Equal(pair.Value, args[Array.IndexOf(args, pair.Key) + 1]);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("cache")]
    [InlineData("nan")]
    [InlineData("output")]
    public void InvalidModelSettingsCannotLaunch(string failure)
    {
        var settings = failure switch
        {
            "batch" => new ModelSettings { BatchSize = 16, MicroBatchSize = 32 },
            "cache" => new() { FlashAttention = false, CacheTypeV = "q8_0" },
            "nan" => new() { Temperature = double.NaN },
            _ => new() { MaxOutputTokens = 0 }
        };
        Assert.Throws<ArgumentException>(() => new RuntimePlanner().Build(new() { ModelSettings = settings }));
    }

    [AvaloniaFact]
    public async Task ModelEditorsPersistAndRestoreThroughWindowLifecycle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "open-llm-cluster-settings-" + Guid.NewGuid().ToString("N"));
        var store = new ProfileStore(Path.Combine(directory, "profile.json"));
        var app = (App)Application.Current!;
        var window = app.CreateMainWindow(store);
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("RuntimeTab");
            window.FindControl<NumericUpDown>("MaxOutputTokensEditor")!.Value = 2048;
            Assert.Null(window.FindControl<ComboBox>("EffortSelector"));
            Dispatcher.UIThread.RunJobs();
            vm.Temperature = 0.4;
            vm.CacheTypeK = "q8_0";
            vm.ReasoningBudget = 1024;
            vm.AutoMonitor = false;
            vm.CommandPreview = "old";
            vm.BatchSize = 256;
            Assert.Empty(vm.CommandPreview);
            window.Close();
            var reopened = app.CreateMainWindow(store);
            try
            {
                var restored = (MainWindowViewModel)reopened.DataContext!;
                Assert.Equal(2048, restored.MaxOutputTokens);
                Assert.Equal("default", restored.ReasoningEffort);
                Assert.Equal(0.4, restored.Temperature);
                Assert.Equal("q8_0", restored.CacheTypeK);
                Assert.Equal(1024, restored.ReasoningBudget);
                Assert.Equal(256, restored.BatchSize);
                Assert.False(restored.AutoMonitor);
                Assert.False(restored.IsEngineRunning);
                Assert.Equal(2048, (await store.LoadAsync()).ModelSettings.MaxOutputTokens);
            }
            finally { reopened.Close(); }
        }
        finally { if (window.IsVisible) window.Close(); Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EffortSupportComesFromActualTemplateCapabilities(bool supported)
    {
        using var client = new HttpClient(new CapsHandler(supported));
        var caps = await new EngineApiClient(client).ReadCapabilitiesAsync(new("http://localhost/"), "test-key", TestContext.Current.CancellationToken);
        Assert.Equal(supported, caps.SupportsEffort);
        Assert.Equal(supported ? new[] { "low", "high" } : [], caps.EffortLevels);
    }

    [Fact]
    public async Task CancellingCapabilitiesReadMustNotReportSuccessfulCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new CapsHandler(supported: true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EngineApiClient(client)
            .ReadCapabilitiesAsync(new("http://localhost/"), "test-key", cancellation.Token));
    }

    private sealed class CapsHandler(bool supported) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/props", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { chat_template_caps = new { supports_reasoning_effort = supported, reasoning_effort_levels = supported ? new[] { "low", "high" } : [] } })) });
        }
    }
}
