namespace OpenLLMCluster.Desktop.Services;

/// <summary>Предоставляет единое название и версию приложения из метаданных сборки</summary>
public static class ApplicationInfo
{
    public const string Name = "Open LLM Cluster";
    public const string ShortName = "OLC";
    public static string Version { get; } = typeof(ApplicationInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? typeof(ApplicationInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    public static string WindowTitle => $"{Name} v{Version}";
}
