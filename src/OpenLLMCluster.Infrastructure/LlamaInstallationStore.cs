namespace OpenLLMCluster.Infrastructure;

/// <summary>Установленная и проверенная сборка; секреты не входят в манифест</summary>
public sealed record LlamaInstallation(string ReleaseTag, LlamaBackend Backend, string Platform, string Directory, string VersionOutput);

/// <summary>Хранит выбор управляемой сборки для Desktop и локального агента</summary>
public sealed class LlamaInstallationStore(string? root = null)
{
    public string Root { get; } = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Open-LLM-Cluster", "runtimes");

    /// <summary>Читает последнюю установленную сборку, доступную агенту того же пользователя</summary>
    public LlamaInstallation? ReadCurrent() => Read(Path.Combine(Root, "current.json"));

    /// <summary>Читает происхождение сборки из выбранного каталога</summary>
    public static LlamaInstallation? ReadDirectory(string directory) => Read(Path.Combine(directory, "open-llm-cluster-build.json"));

    /// <summary>Сохраняет манифест каталога и указатель выбора пользователя</summary>
    public void Save(LlamaInstallation installation)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var text = JsonSerializer.Serialize(installation, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(installation.Directory, "open-llm-cluster-build.json"), text);
            File.WriteAllText(Path.Combine(Root, "current.json"), text);
        }
        catch (Exception ex) { Trace.TraceError("Запись сборки llama.cpp: {0}", ex.GetType().Name); throw; }
    }

    // Отсутствие манифеста означает неуправляемую сборку, повреждение не скрывается
    private static LlamaInstallation? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<LlamaInstallation>(File.ReadAllText(path)) : null; }
        catch (Exception ex) { Trace.TraceError("Чтение сборки llama.cpp: {0}", ex.GetType().Name); throw; }
    }
}
