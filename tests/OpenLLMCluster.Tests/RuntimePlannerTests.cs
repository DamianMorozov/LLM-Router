// Проверяет наблюдаемые результаты проверки GGUF и аргументы запуска для разных ролей
namespace OpenLLMCluster.Tests;

public sealed class RuntimePlannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "open-llm-cluster-tests-" + Guid.NewGuid().ToString("N"));

    public RuntimePlannerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CoordinatorIncludesOnlyEnabledRpcEndpointsAndPreservesModelPath()
    {
        var profile = await CreateProfileAsync();
        profile = profile with
        {
            Nodes = [new() { Name = "worker", Host = "worker.example", Enabled = true }, new() { Name = "off", Host = "off.example", Enabled = false }]
        };
        var results = await new RuntimePlanner().ValidateAsync(profile, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(results, r => r.IsError);
        var plan = new RuntimePlanner().Build(profile);
        Assert.Equal(profile.ModelPath, plan.Arguments[Array.IndexOf(plan.Arguments, "--model") + 1]);
        Assert.Equal("worker.example:50052", plan.Arguments[Array.IndexOf(plan.Arguments, "--rpc") + 1]);
        Assert.DoesNotContain("off.example", plan.ToDisplayString());
    }

    [Fact]
    public async Task WorkerDoesNotRequireModelOrForwardOtherWorkers()
    {
        var profile = new RouterProfile
        {
            Role = NodeRole.Worker, RuntimeDirectory = _directory, ModelPath = "missing.gguf",
            Nodes = [new() { Name = "unfinished coordinator node", Host = "" }]
        };
        await File.WriteAllTextAsync(Path.Combine(_directory, RuntimePlanner.GetExecutableName(NodeRole.Worker)), "test executable marker",
            TestContext.Current.CancellationToken);
        var results = await new RuntimePlanner().ValidateAsync(profile, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(results, r => r.IsError);
        var plan = new RuntimePlanner().Build(profile);
        Assert.Contains("--cache", plan.Arguments);
        Assert.DoesNotContain("--model", plan.Arguments);
        Assert.DoesNotContain("--rpc", plan.Arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task InvalidPortBlocksValidation(int port)
    {
        var profile = (await CreateProfileAsync()) with { ApiPort = port };
        Assert.Contains(await new RuntimePlanner().ValidateAsync(profile, TestContext.Current.CancellationToken), r => r.Code == "InvalidPort" && r.IsError);
        Assert.Throws<ArgumentException>(() => new RuntimePlanner().Build(profile));
    }

    [Fact]
    public async Task InvalidGgufIsReportedEvenWhenExtensionMatches()
    {
        var profile = await CreateProfileAsync();
        await File.WriteAllTextAsync(profile.ModelPath, "not a model", TestContext.Current.CancellationToken);
        Assert.Contains(await new RuntimePlanner().ValidateAsync(profile, TestContext.Current.CancellationToken), r => r.Code == "InvalidGguf" && r.IsError);
    }

    [Fact]
    public async Task HeaderCheckDoesNotClaimFullModelCompatibility()
    {
        var results = await new RuntimePlanner().ValidateAsync(await CreateProfileAsync(), TestContext.Current.CancellationToken);
        Assert.Contains(results, r => r.Code == "GgufHeaderFound");
        Assert.Contains(results, r => r.Code == "CompatibilityNotVerified");
    }

    [Fact]
    public async Task DuplicateEndpointsBlockValidation()
    {
        var profile = (await CreateProfileAsync()) with
        {
            Nodes = [new() { Name = "a", Host = "worker.example" }, new() { Name = "b", Host = "WORKER.example" }]
        };
        Assert.Contains(await new RuntimePlanner().ValidateAsync(profile, TestContext.Current.CancellationToken), r => r.Code == "DuplicateNode" && r.IsError);
    }

    [Theory]
    [InlineData("bad host")]
    [InlineData("worker.example,--rpc=other")]
    [InlineData("http://worker.example")]
    public void InvalidHostCannotBecomeAnRpcArgument(string host)
    {
        var profile = new RouterProfile { Nodes = [new() { Name = "worker", Host = host }] };
        Assert.Throws<ArgumentException>(() => new RuntimePlanner().Build(profile));
    }

    // Подготавливает только сигнатуру для проверки заголовка, не имитируя рабочую модель
    private async Task<RouterProfile> CreateProfileAsync()
    {
        var path = Path.Combine(_directory, "test model.gguf");
        await File.WriteAllBytesAsync(path, [71, 71, 85, 70, 3, 0, 0, 0], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_directory, RuntimePlanner.GetExecutableName(NodeRole.Coordinator)), "test executable marker",
            TestContext.Current.CancellationToken);
        return new() { RuntimeDirectory = _directory, ModelPath = path };
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
