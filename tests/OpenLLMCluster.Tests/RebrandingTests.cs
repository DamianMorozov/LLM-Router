// Проверяет имена сборок, пользовательского хранилища и API после полного переименования проекта
namespace OpenLLMCluster.Tests;

public sealed class RebrandingTests
{
    [Fact]
    public void AssembliesExposeConsistentProductNames()
    {
        Assert.Equal("OpenLLMCluster.Core", typeof(RouterProfile).Assembly.GetName().Name);
        Assert.Equal("OpenLLMCluster.Infrastructure", typeof(ProfileStore).Assembly.GetName().Name);
        Assert.Equal("OpenLLMCluster.Desktop", typeof(MainWindow).Assembly.GetName().Name);
        Assert.Equal("OpenLLMCluster.Agent", typeof(Agent.Program).Assembly.GetName().Name);
    }

    [Fact]
    public void DefaultStoresUseTheNewProductDirectory()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Open-LLM-Cluster");
        Assert.Equal(Path.Combine(root, "profile.json"), new ProfileStore().FilePath);
        Assert.Equal(Path.Combine(root, "runtimes"), new LlamaInstallationStore().Root);
    }

    [Fact]
    public void CoordinatorAdvertisesTheNewApiModelId()
    {
        var plan = new RuntimePlanner().Build(new() { ModelPath = "model.gguf" });
        Assert.Equal("open-llm-cluster", plan.Arguments[Array.IndexOf(plan.Arguments, "--alias") + 1]);
    }
}
