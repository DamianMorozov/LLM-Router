namespace OpenLLMCluster.Contracts;

/// <summary>Отпечатки всех частей GGUF без локальных путей и содержимого весов</summary>
[MessagePackObject]
public sealed class LocalModelIdentity
{
    [Key(0)] public List<ModelFileIdentity> Files { get; set; } = [];
}

/// <summary>Размер и SHA-256 одной части в порядке загрузки GGUF</summary>
[MessagePackObject]
public sealed class ModelFileIdentity
{
    [Key(0)] public long Length { get; set; }
    [Key(1)] public string Sha256 { get; set; } = "";
}

/// <summary>Результат проверки локального GGUF и подготовки дискового кэша RPC</summary>
[MessagePackObject]
public sealed class LocalModelStatus
{
    [Key(0)] public bool Configured { get; set; }
    [Key(1)] public LocalModelIdentity Identity { get; set; } = new();
    [Key(2)] public int CachedTensors { get; set; }
    [Key(3)] public long CachedBytes { get; set; }
}
