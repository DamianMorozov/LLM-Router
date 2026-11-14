namespace OpenLLMCluster.Agent;

/// <summary>Путь разрешается только пользователем на самом компьютере с агентом</summary>
public sealed record LocalModelPolicy(string Path);

/// <summary>Сверяет модель с главным ПК и подготавливает локальный дисковый кэш RPC</summary>
public sealed class LocalModelManager(LocalModelPolicy policy, LlamaInstallationStore store, RpcModelCache cache)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Не принимает удалённые пути и не загружает файлы по сети</summary>
    public async Task<LocalModelStatus> PrepareAsync(LocalModelIdentity expected, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(policy.Path)) return new();
            var installation = store.ReadCurrent() ?? throw new InvalidOperationException("Сначала установи сборку llama.cpp на узле");
            if (installation.ReleaseTag != LlamaBuildDefaults.ReleaseTag)
                throw new InvalidOperationException("Подготовка локального кэша проверена только для " + LlamaBuildDefaults.ReleaseTag);
            return await Task.Run(() => cache.PrepareAsync(policy.Path, installation.Directory, expected, ct: ct), ct);
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Модель узла: {0}", ex.GetType().Name); throw; }
        finally { _gate.Release(); }
    }
}
