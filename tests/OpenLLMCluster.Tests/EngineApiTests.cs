// Проверяет отказ HTTP, отсутствие готовности при загрузке и маскирование ключа в диагностике контрольного запроса
namespace OpenLLMCluster.Tests;

public sealed class EngineApiTests
{
    [Theory]
    [InlineData(502, "backend not listening", false)]
    [InlineData(503, "loading model", false)]
    [InlineData(200, "loading model", false)]
    [InlineData(200, "ok", true)]
    public async Task HealthAndAuthenticatedModelListAreBothRequired(int code, string status, bool expected)
    {
        using var client = new HttpClient(new ApiHandler((request) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return request.RequestUri!.AbsolutePath == "/health"
                ? new((HttpStatusCode)code) { Content = new StringContent(JsonSerializer.Serialize(new { status })) }
                : new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"open-llm-cluster"}]}""") };
        }));
        Assert.Equal(expected, await new EngineApiClient(client).IsReadyAsync(new("http://localhost/"), "test-key", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret-key")]
    public async Task ApiErrorPreservesHttpStatusAndRedactsNonemptyKey(string key)
    {
        using var client = new HttpClient(new ApiHandler(_ => new(HttpStatusCode.BadRequest) { Content = new StringContent("bad input " + key) }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new EngineApiClient(client)
            .TestAsync(new("http://localhost/"), key, "READY", TestContext.Current.CancellationToken));
        Assert.Contains("400", error.Message);
        if (key.Length > 0) Assert.DoesNotContain(key, error.Message);
    }

    [Fact]
    public async Task HealthyServerWithDifferentModelIsNotReady()
    {
        using var client = new HttpClient(new ApiHandler(request => new(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath == "/health"
                ? """{"status":"ok"}""" : """{"data":[{"id":"other-model"}]}""")
        }));
        Assert.False(await new EngineApiClient(client).IsReadyAsync(new("http://localhost/"), "secret", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuthenticationErrorCannotBecomeReadyStatus()
    {
        using var client = new HttpClient(new ApiHandler(_ => new(HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new EngineApiClient(client)
            .IsReadyAsync(new("http://localhost/"), "wrong-key", TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task EmptyResponseIsNotReportedAsPassedGeneration()
    {
        using var client = new HttpClient(new ApiHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("""{"choices":[{"message":{"content":""}}]}""") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new EngineApiClient(client)
            .TestAsync(new("http://localhost/"), "secret", "READY", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReasoningOnlyResponseReturnsActualGeneratedTextAndUsage()
    {
        using var client = new HttpClient(new ApiHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("""{"choices":[{"message":{"content":null,"reasoning_content":"Thinking"}}],"usage":{"completion_tokens":2}}""") }));
        var result = await new EngineApiClient(client).TestAsync(new("http://localhost/"), "secret", "READY", TestContext.Current.CancellationToken);
        Assert.Equal("Thinking", result.Text);
        Assert.Equal(2, result.CompletionTokens);
    }

    private sealed class ApiHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
