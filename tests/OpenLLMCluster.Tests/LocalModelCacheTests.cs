// Проверяет сверку локальных весов, подготовку настоящих байтов кэша RPC, повторное использование и отмену
namespace OpenLLMCluster.Tests;

public sealed class LocalModelCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "olc-local-model-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task IdentityReadsActualBytesAndRejectsDifferentWeightsBeforePreparingCache()
    {
        var file = CreateModel("model.gguf", 64);
        var identity = await RpcModelCache.IdentifyAsync(file, TestContext.Current.CancellationToken);
        var part = Assert.Single(identity.Files);
        Assert.Equal(64, part.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))), part.Sha256);
        var changed = File.ReadAllBytes(file);
        changed[^1] = 1;
        File.WriteAllBytes(file, changed);
        var parsed = false;
        var cache = new RpcModelCache(Path.Combine(_root, "cache"), (_, _) => { parsed = true; return []; });
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.PrepareAsync(file, "runtime", identity, ct: TestContext.Current.CancellationToken));
        Assert.False(parsed);
        Assert.False(Directory.Exists(cache.CacheRoot));
    }

    [Fact]
    public async Task TensorCacheContainsExactWeightBytesAndIsReusedWithoutReadingLayoutAgain()
    {
        var file = CreateModel("модель.gguf", 11 * 1024 * 1024 + 8);
        var reads = 0;
        var cache = new RpcModelCache(Path.Combine(_root, "cache"), (_, _) => { reads++; return [new(8, 11 * 1024 * 1024)]; });
        var expected = await RpcModelCache.IdentifyAsync(file, TestContext.Current.CancellationToken);
        var result = await cache.PrepareAsync(file, "runtime", expected, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Configured);
        Assert.Equal(1, result.CachedTensors);
        Assert.Equal(11 * 1024 * 1024, result.CachedBytes);
        var tensor = Assert.Single(Directory.GetFiles(Path.Combine(cache.CacheRoot, "rpc")));
        Assert.Matches("^[0-9a-f]{16}$", Path.GetFileName(tensor));
        Assert.Equal(File.ReadAllBytes(file).Skip(8), File.ReadAllBytes(tensor));
        var timestamp = File.GetLastWriteTimeUtc(tensor);
        await cache.PrepareAsync(file, "runtime", expected, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, reads);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(tensor));
        File.Delete(tensor);
        await cache.PrepareAsync(file, "runtime", expected, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, reads);
        Assert.True(File.Exists(tensor));
    }

    [Fact]
    public async Task SplitIdentityIncludesEveryPartAndRejectsMissingPartsAndNonFirstSelection()
    {
        var first = CreateModel("weights-00001-of-00002.gguf", 32);
        var second = CreateModel("weights-00002-of-00002.gguf", 48);
        var identity = await RpcModelCache.IdentifyAsync(first, TestContext.Current.CancellationToken);
        Assert.Equal(new long[] { 32, 48 }, identity.Files.Select(file => file.Length));
        await Assert.ThrowsAsync<InvalidDataException>(() => RpcModelCache.IdentifyAsync(second, TestContext.Current.CancellationToken));
        File.Delete(second);
        await Assert.ThrowsAsync<FileNotFoundException>(() => RpcModelCache.IdentifyAsync(first, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationDoesNotCreateSuccessfulCacheMarker()
    {
        var file = CreateModel("model.gguf", 64);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var cache = new RpcModelCache(Path.Combine(_root, "cache"), (_, _) => []);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.PrepareAsync(file, "runtime", ct: cancel.Token));
        Assert.False(Directory.Exists(cache.CacheRoot));
    }

    [Fact]
    public async Task SizeAndPartCountMismatchesAreRejectedEvenWithSameDigest()
    {
        var file = CreateModel("model.gguf", 64);
        var identity = await RpcModelCache.IdentifyAsync(file, TestContext.Current.CancellationToken);
        var part = Assert.Single(identity.Files);
        var wrongSize = new LocalModelIdentity { Files = [new() { Length = 65, Sha256 = part.Sha256 }] };
        Assert.Throws<InvalidDataException>(() => RpcModelCache.RequireMatch(wrongSize, identity));
        Assert.Throws<InvalidDataException>(() => RpcModelCache.RequireMatch(new(), identity));
    }

    // Создаёт файл с заголовком GGUF и синтетическими весами без скачивания модели
    private string CreateModel(string name, int size)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, name);
        var bytes = new byte[size];
        "GGUF"u8.CopyTo(bytes);
        bytes[4] = 3;
        File.WriteAllBytes(file, bytes);
        return file;
    }

    /// <summary>Удаляет только временные данные этой проверки</summary>
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
