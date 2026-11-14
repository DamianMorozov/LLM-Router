// Проверяет поиск в изолированных каталогах: настройки LM Studio, разные исполняемые файлы и отсутствие обхода весов
namespace OpenLLMCluster.Tests;

public sealed class InstallationDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "open-llm-cluster-discovery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FindsConfiguredModelsAndDistinguishesStudioServerFromStandaloneRpc()
    {
        var models = CreateDirectory("external models");
        var studio = CreateDirectory("home/.lmstudio/extensions/backends/llama.cpp-test");
        File.WriteAllText(Path.Combine(studio, RuntimePlanner.GetExecutableName(NodeRole.Coordinator)), "test fixture");
        var standalone = CreateDirectory("standalone runtime");
        File.WriteAllText(Path.Combine(standalone, RuntimePlanner.GetExecutableName(NodeRole.Worker)), "test fixture");
        WriteSettings(JsonSerializer.Serialize(new { downloadsFolder = models, hfDownloadToken = "unused-secret" }));
        var service = CreateService(standalone + Path.PathSeparator + standalone);
        var found = await service.FindAsync(ct: TestContext.Current.CancellationToken);
        Assert.False(found.HasReadErrors);
        Assert.Equal(models, Assert.Single(found.ModelDirectories));
        Assert.Equal(2, found.Runtimes.Count);
        var backend = Assert.Single(found.Runtimes, item => item.Directory == studio);
        Assert.True(backend.HasServer);
        Assert.False(backend.HasRpc);
        var rpc = Assert.Single(found.Runtimes, item => item.Directory == standalone);
        Assert.False(rpc.HasServer);
        Assert.True(rpc.HasRpc);
    }

    [Fact]
    public async Task MalformedSettingsStillFindsFallbackAndReportsReadError()
    {
        var models = CreateDirectory("home/.lmstudio/models");
        WriteSettings("{broken");
        var found = await CreateService().FindAsync(ct: TestContext.Current.CancellationToken);
        Assert.True(found.HasReadErrors);
        Assert.Equal(models, Assert.Single(found.ModelDirectories));
        Assert.Empty(found.Runtimes);
    }

    [Fact]
    public async Task EmptyLegacySettingsDoNotMarkSuccessfulDiscoveryAsUnreadable()
    {
        var models = CreateDirectory("models");
        WriteSettings(JsonSerializer.Serialize(new { downloadsFolder = models }));
        File.WriteAllText(Path.Combine(CreateDirectory("appdata/LM Studio"), "settings.json"), "");
        var found = await CreateService().FindAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(models, Assert.Single(found.ModelDirectories));
        Assert.False(found.HasReadErrors);
    }

    [Theory]
    [InlineData("../models")]
    [InlineData("~/models")]
    public async Task RelativeAndHomeModelPathsAreResolved(string configured)
    {
        var models = CreateDirectory("home/models");
        WriteSettings(JsonSerializer.Serialize(new { downloadsFolder = configured }));
        var found = await CreateService().FindAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(models, Assert.Single(found.ModelDirectories));
        Assert.False(found.HasReadErrors);
    }

    [Fact]
    public async Task MissingConfiguredDirectoryIsNotSuggestedAndManualDirectoryKeepsPriority()
    {
        WriteSettings(JsonSerializer.Serialize(new { downloadsFolder = Path.Combine(_root, "missing") }));
        var manual = CreateDirectory("manual models");
        var fallback = CreateDirectory("home/.lmstudio/models");
        var found = await CreateService().FindAsync(modelDirectory: manual, ct: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { manual, fallback }, found.ModelDirectories);
    }

    [Fact]
    public async Task CancelledSearchDoesNotReturnPartialResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().FindAsync(ct: cancellation.Token));
    }

    // Отделяет проверку от домашнего каталога и PATH разработчика
    private LocalInstallationDiscovery CreateService(string path = "")
        => new(Path.Combine(_root, "home"), Path.Combine(_root, "app"), path, Path.Combine(_root, "appdata"));

    // Создаёт небольшой каталог без настоящих моделей или исполняемых программ
    private string CreateDirectory(string relative)
    {
        var directory = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(directory);
        return directory;
    }

    // Записывает только тестовые настройки LM Studio
    private void WriteSettings(string content)
        => File.WriteAllText(Path.Combine(CreateDirectory("home/.lmstudio"), "settings.json"), content);

    /// <summary>Удаляет временное окружение теста</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
