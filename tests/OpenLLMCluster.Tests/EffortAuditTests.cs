// Проверяет ограничение выбора Effort уровнями действительно загруженного шаблона
namespace OpenLLMCluster.Tests;

public sealed class EffortAuditTests
{
    [AvaloniaFact]
    public async Task LoadedTemplateRestrictsEffortChoicesAndResetsUnsupportedSelection()
    {
        using var fixture = new EngineProcessTests();
        var profile = fixture.CreateProfile(NodeRole.Coordinator);
        using var host = new EngineProcessHost(EngineProcessTests.CreateRunner("effort"));
        using var vm = new MainWindowViewModel(new ProfileStore(Path.Combine(profile.RuntimeDirectory, "profile.json")), engineHost: host)
        { RuntimeDirectory = profile.RuntimeDirectory, ModelPath = profile.ModelPath, ApiPort = profile.ApiPort, ReasoningEffort = "xhigh" };
        await vm.StartEngineCommand.ExecuteAsync(null);
        Assert.True(vm.IsEngineRunning, vm.EngineStatus);
        Assert.Equal(new[] { "low", "high" }, vm.EffortChoices);
        Assert.Equal("default", vm.ReasoningEffort);
        vm.StopEngineCommand.Execute(null);
        Assert.False(vm.CanSelectEffort);
        vm.ModelPath += ".other";
        Assert.Empty(vm.EffortChoices);
    }
}
