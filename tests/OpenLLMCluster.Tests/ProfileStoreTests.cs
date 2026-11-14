// Проверяет сохранение профиля, чтение ошибок и отсутствие секретов в файле настроек
namespace OpenLLMCluster.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "open-llm-cluster-profile-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProfileRoundTripPreservesRoleThemeLanguageAndNodes()
    {
        var store = new ProfileStore(Path.Combine(_directory, "profile.json"));
        var profile = new RouterProfile
        {
            Language = "en", Theme = AppTheme.Dark, Role = NodeRole.Worker,
            Nodes = [new() { Name = "worker", Host = "worker.example", AgentPort = 5101, RpcPort = 50053 }]
        };
        await store.SaveAsync(profile, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(profile.Role, loaded.Role);
        Assert.Equal(profile.Theme, loaded.Theme);
        Assert.Equal(profile.Language, loaded.Language);
        Assert.Equal(profile.Nodes[0], loaded.Nodes[0]);
    }

    [Fact]
    public async Task FirstLaunchDoesNotCreateAFile()
    {
        var store = new ProfileStore(Path.Combine(_directory, "profile.json"));
        Assert.Empty((await store.LoadAsync(TestContext.Current.CancellationToken)).Nodes);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task ExistingProfileWithoutWindowGeometryRemainsCompatible()
    {
        Directory.CreateDirectory(_directory);
        var store = new ProfileStore(Path.Combine(_directory, "profile.json"));
        await File.WriteAllTextAsync(store.FilePath, """{"SchemaVersion":1,"Language":"en","Theme":"Dark","ContextLength":16384}""",
            TestContext.Current.CancellationToken);
        var vm = new MainWindowViewModel(store);
        vm.Initialize();
        Assert.True(vm.RememberSettings);
        Assert.Equal(2, vm.LanguageIndex);
        Assert.Equal(2, vm.ThemeIndex);
        Assert.Equal(16384, vm.ContextLength);
        Assert.Equal(new WindowGeometry(), vm.WindowGeometry);
        Assert.False(File.Exists(store.StartupFilePath));
    }

    [Fact]
    public async Task ExplicitSaveWorksWithAutomaticPersistenceDisabled()
    {
        var store = new ProfileStore(Path.Combine(_directory, "profile.json"));
        store.SaveAutomaticSettings(enabled: false);
        var vm = new MainWindowViewModel(store);
        vm.Initialize();
        Assert.False(vm.RememberSettings);
        vm.ContextLength = 16384;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(16384, store.Load().ContextLength);
        vm.ContextLength = 32768;
        vm.SaveOnClose();
        Assert.Equal(16384, store.Load().ContextLength);
        var reopened = new MainWindowViewModel(store);
        reopened.Initialize();
        Assert.False(reopened.RememberSettings);
        Assert.Equal(8192, reopened.ContextLength);
    }

    [Fact]
    public async Task CorruptProfileIsNotSilentlyReplaced()
    {
        Directory.CreateDirectory(_directory);
        var store = new ProfileStore(Path.Combine(_directory, "profile.json"));
        await File.WriteAllTextAsync(store.FilePath, "broken json", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<JsonException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("broken json", await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SavingFromViewModelNeverWritesAgentToken()
    {
        var store = new ProfileStore(Path.Combine(_directory, "profile.json"));
        var vm = new MainWindowViewModel(store);
        vm.Nodes.Add(new(new() { Name = "worker", Host = "worker.example" }, vm.Text) { AccessToken = "secret-that-must-not-be-saved" });
        await vm.SaveCommand.ExecuteAsync(null);
        var json = await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret-that-must-not-be-saved", json);
        Assert.DoesNotContain("AccessToken", json);
        Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken)).Nodes);
    }

    [Fact]
    public async Task ViewModelRestoresPersistedLanguageRoleThemeAndContext()
    {
        var store = new ProfileStore(Path.Combine(_directory, "profile.json"));
        await store.SaveAsync(new() { Language = "en", Theme = AppTheme.Dark, Role = NodeRole.Worker, ContextLength = 32768 },
            TestContext.Current.CancellationToken);
        var vm = new MainWindowViewModel(store);
        await vm.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("en", vm.Text.Language);
        Assert.Equal(2, vm.ThemeIndex);
        Assert.Equal(1, vm.RoleIndex);
        Assert.Equal(32768, vm.ContextLength);
        Assert.Equal(vm.Text["ProfileLoaded"], vm.Status);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
