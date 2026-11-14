// Воспроизводит недостающие сохранение ключей и проверку повреждённых новых параметров профиля
namespace OpenLLMCluster.Tests;

public sealed class AuditRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "open-llm-cluster-audit-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public void AgentKeysSurviveRememberedProfileWithoutPlainTextInProfile()
    {
        var store = new ProfileStore(Path.Combine(_root, "profile.json"));
        using var vm = new MainWindowViewModel(store);
        vm.LocalAgentToken = "local-audit-secret";
        vm.Nodes.Add(new(new() { Name = "test", Host = "worker.example" }, vm.Text) { AccessToken = "remote-audit-secret" });
        vm.SaveOnClose();
        using var reopened = new MainWindowViewModel(store);
        reopened.Initialize();
        Assert.Equal("local-audit-secret", reopened.LocalAgentToken);
        Assert.Equal("remote-audit-secret", Assert.Single(reopened.Nodes).AccessToken);
        var json = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("local-audit-secret", json);
        Assert.DoesNotContain("remote-audit-secret", json);
    }

    [Fact]
    public void NullModelSettingsAreRejectedAsCorruptProfile()
    {
        Directory.CreateDirectory(_root);
        var store = new ProfileStore(Path.Combine(_root, "profile.json"));
        File.WriteAllText(store.FilePath, """{"SchemaVersion":1,"ModelSettings":null}""");
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    /// <summary>Удаляет только временный профиль теста</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
