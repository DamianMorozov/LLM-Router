// Проверяет справку Effort и обработку настоящих запросов клиента с возвратом к настройке шаблона
namespace OpenLLMCluster.Tests;

public sealed class ClientEffortTests
{
    [AvaloniaFact]
    public async Task EffortIsReadOnlyAndCopiesOnlyAdvertisedLevels()
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner("effort"));
        using var vm = new MainWindowViewModel(new ProfileStore(Path.Combine(profile.RuntimeDirectory, "profile.json")), engineHost: host)
        { RuntimeDirectory = profile.RuntimeDirectory, ModelPath = profile.ModelPath, ApiPort = profile.ApiPort };
        vm.LanguageIndex = 1;
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Assert.Null(window.FindControl<ComboBox>("EffortSelector"));
            Assert.NotNull(window.FindControl<SelectableTextBlock>("EffortValues"));
            await vm.StartEngineCommand.ExecuteAsync(null);
            Assert.True(vm.IsEngineRunning, vm.EngineStatus);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("low, high", window.FindControl<SelectableTextBlock>("EffortValues")!.Text);
            var copy = window.FindControl<Button>("CopyEffortButton")!;
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("RuntimeTab");
            Dispatcher.UIThread.RunJobs();
            Assert.True(copy.IsEffectivelyEnabled);
            await Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(copy.Command).ExecuteAsync(copy.CommandParameter);
            Assert.Equal("low, high", await window.Clipboard!.TryGetTextAsync());
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", vm.ServerApiKey);
            using var response = await client.PostAsync(new Uri(host.BaseUri!, "v1/chat/completions"), new StringContent(
                """{"model":"open-llm-cluster","max_tokens":4,"messages":[{"role":"user","content":"READY"}],"reasoning_effort":"on"}""",
                Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Effort", window.FindControl<TextBlock>("ModelEffortNotice")!.Text);
            Assert.Contains("по умолчанию", window.FindControl<TextBlock>("ModelEffortNotice")!.Text);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData("on", "default")]
    [InlineData("xhigh", "default")]
    [InlineData("default", "default")]
    public async Task ClientEffortReachesBackendOrFallsBack(string sent, string expected)
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner("effort"));
        var lines = new List<string>();
        host.OutputReceived += line => { lock (lines) lines.Add(line); };
        await host.StartAsync(profile, "client-effort-key", TestContext.Current.CancellationToken);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "client-effort-key");
        var payload = JsonSerializer.Serialize(new { model = "open-llm-cluster", max_tokens = 4,
            messages = new[] { new { role = "user", content = "READY" } }, reasoning_effort = sent });
        using var response = await client.PostAsync(new Uri(host.BaseUri!, "v1/chat/completions"),
            new StringContent(payload, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, body.RootElement.GetProperty("observed_effort").GetString());
        if (expected == "default" && sent != "default")
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            lock (lines) Assert.Contains(lines, line => line == "OLC_EFFORT_FALLBACK");
        }
    }

    [Theory]
    [InlineData("v1/chat/completions", "\"reasoning_effort\":42", "default")]
    [InlineData("v1/chat/completions", "\"chat_template_kwargs\":{\"reasoning_effort\":\"on\"}", "default")]
    [InlineData("v1/responses", "\"reasoning\":{\"effort\":\"high\"}", "high")]
    [InlineData("v1/responses", "\"reasoning\":{\"effort\":\"on\"}", "default")]
    public async Task NestedAndMalformedValuesWorkWithStreaming(string endpoint, string effort, string expected)
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner("effort"));
        await host.StartAsync(profile, "stream-key", TestContext.Current.CancellationToken);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "stream-key");
        var payload = "{\"model\":\"open-llm-cluster\",\"max_tokens\":4,\"stream\":true," + effort + "}";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(host.BaseUri!, endpoint))
        { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        Assert.Contains(expected, await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Contains("[DONE]", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-key");
        using var rejected = await client.GetAsync(new Uri(host.BaseUri!, "v1/models"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        host.Stop();
        Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", profile.ApiPort, TestContext.Current.CancellationToken)).IsOpen);
    }

    [Theory]
    [InlineData("effort-reject", 200, "default")]
    [InlineData("effort-context", 400, "context window")]
    public async Task RetryIsLimitedToEffortErrors(string mode, int status, string expected)
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner(mode));
        var requests = 0;
        host.OutputReceived += line => { if (line == "CHAT_REQUEST_OK") Interlocked.Increment(ref requests); };
        await host.StartAsync(profile, "retry-key", TestContext.Current.CancellationToken);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "retry-key");
        using var response = await client.PostAsync(new Uri(host.BaseUri!, "v1/chat/completions"), new StringContent(
            """{"model":"open-llm-cluster","max_tokens":4,"reasoning_effort":"low"}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(mode == "effort-reject" ? 2 : 1, requests);
    }
}
