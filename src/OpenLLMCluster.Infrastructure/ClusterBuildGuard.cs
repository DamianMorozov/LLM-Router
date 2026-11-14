namespace OpenLLMCluster.Infrastructure;

/// <summary>Проверяет одинаковый build до передачи весов модели вычислительным узлам</summary>
public static class ClusterBuildGuard
{
    /// <summary>Блокирует смесь версий и неподготовленные узлы; backend может отличаться при одинаковом build</summary>
    public static void RequireCompatible(string expectedTag, LlamaInstallation? local, NodeSnapshot remote, string nodeName)
    {
        if (local?.ReleaseTag != expectedTag) throw new InvalidOperationException("Установи на главном ПК управляемую сборку " + expectedTag);
        if (remote.LlamaReleaseTag != expectedTag || !remote.LlamaRpcAvailable)
            throw new InvalidOperationException("Узел " + nodeName + ": требуется установить " + expectedTag
                + " с RPC через Desktop узла и обновить Agent; обнаружено: " + (remote.LlamaReleaseTag.Length > 0 ? remote.LlamaReleaseTag : "нет сборки"));
    }
}
