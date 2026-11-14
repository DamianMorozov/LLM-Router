// Проверяет защищённое сохранение ключей, удаление старых значений и приоритет общего переключателя настроек
namespace OpenLLMCluster.Tests;

public sealed class SecretPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "open-llm-cluster-keys-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SecretStoreRoundTripsWithoutPlainTextAndRejectsCorruption()
    {
        var store = new ProtectedSecretStore(Path.Combine(_root, "keys.dat"));
        const string secret = "secret-that-must-not-be-on-disk-in-clear";
        store.Save(new() { ["worker"] = secret });
        Assert.Equal(secret, store.Load()["worker"]);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(store.FilePath)));
        File.WriteAllBytes(store.FilePath, [1, 2, 3]);
        Assert.ThrowsAny<Exception>(() => store.Load());
    }

    [AvaloniaFact]
    public void DisablingKeyPersistenceClearsPreviousSetWhileProfileStillSaves()
    {
        var store = new ProfileStore(Path.Combine(_root, "profile.json"));
        using (var vm = new MainWindowViewModel(store))
        {
            vm.LocalAgentToken = "old-local-key";
            vm.Nodes.Add(new(new() { Name = "old", Host = "old.example" }, vm.Text) { AccessToken = "old-remote-key" });
            vm.SaveOnClose();
            Assert.Equal(2, store.Secrets.Load().Count);
            vm.RememberAgentKeys = false;
            vm.SaveOnClose();
        }
        Assert.Empty(store.Secrets.Load());
        using var reopened = new MainWindowViewModel(store);
        reopened.Initialize();
        Assert.False(reopened.RememberAgentKeys);
        Assert.Empty(reopened.LocalAgentToken);
        Assert.Empty(reopened.Nodes[0].AccessToken);
    }

    [AvaloniaFact]
    public void GeneralSwitchPreventsSecretReadsAndWrites()
    {
        var store = new ProfileStore(Path.Combine(_root, "profile.json"));
        store.Save(new() { Language = "en" });
        store.Secrets.Save(new() { ["local"] = "keep-existing-key" });
        var original = File.ReadAllBytes(store.Secrets.FilePath);
        using (var vm = new MainWindowViewModel(store))
        {
            vm.Initialize();
            vm.RememberSettings = false;
            vm.LocalAgentToken = "unsaved-key";
            vm.SaveOnClose();
        }
        Assert.Equal(original, File.ReadAllBytes(store.Secrets.FilePath));
        File.WriteAllBytes(store.Secrets.FilePath, [1, 2, 3]);
        using var reopened = new MainWindowViewModel(store);
        reopened.Initialize();
        Assert.False(reopened.RememberSettings);
        Assert.Equal(0, reopened.LanguageIndex);
        Assert.Empty(reopened.LocalAgentToken);
        Assert.Empty(reopened.Events);
        reopened.SaveOnClose();
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(store.Secrets.FilePath));
        Assert.Equal("en", store.Load().Language);
    }

    [AvaloniaFact]
    public void RemovingNodeRemovesItsSavedKey()
    {
        var store = new ProfileStore(Path.Combine(_root, "profile.json"));
        using var vm = new MainWindowViewModel(store);
        vm.Nodes.Add(new(new() { Name = "removed", Host = "removed.example" }, vm.Text) { AccessToken = "removed-key" });
        vm.SaveOnClose();
        Assert.Contains("removed-key", store.Secrets.Load().Values);
        vm.Nodes.Clear();
        vm.SaveOnClose();
        Assert.DoesNotContain("removed-key", store.Secrets.Load().Values);
    }

    /// <summary>Удаляет только изолированный каталог проверки</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
