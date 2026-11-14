namespace OpenLLMCluster.Core;

/// <summary>Роль компьютера в распределённом запуске</summary>
public enum NodeRole { Coordinator, Worker }

/// <summary>Оформление приложения</summary>
public enum AppTheme { System, Light, Dark }

/// <summary>Ускоритель официального пакета llama.cpp</summary>
public enum LlamaBackend { Cuda, Cpu, Metal }

/// <summary>Единая исходная версия для компьютеров кластера; обновляется явно</summary>
public static class LlamaBuildDefaults
{
    public const string ReleaseTag = "b12318";
}

/// <summary>Настройки без паролей, ключей и персональных значений по умолчанию</summary>
public sealed record RouterProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Language { get; init; } = "system";
    public AppTheme Theme { get; init; } = AppTheme.System;
    public WindowGeometry Window { get; init; } = new();
    public AgentSettings Agent { get; init; } = new();
    public NodeRole Role { get; init; }
    public string RuntimeDirectory { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public string ModelDirectory { get; init; } = "";
    public string LlamaReleaseTag { get; init; } = LlamaBuildDefaults.ReleaseTag;
    public LlamaBackend LlamaBackend { get; init; } = LlamaBackend.Cuda;
    public int GpuLayers { get; init; } = -1;
    public int StartupTimeoutSeconds { get; init; } = 900;
    public string BindAddress { get; init; } = "127.0.0.1";
    public int ApiPort { get; init; } = 8080;
    public int RpcPort { get; init; } = 50052;
    public int ContextLength { get; init; } = 8192;
    public int CpuThreads { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    public ModelSettings ModelSettings { get; init; } = new();
    public WorkloadMode Workload { get; init; } = WorkloadMode.Custom;
    public bool AutoMonitor { get; init; } = true;
    public int MonitorIntervalSeconds { get; init; } = 3;
    public bool RememberAgentKeys { get; init; } = true;
    public List<NodeEndpoint> Nodes { get; init; } = [];
}

/// <summary>Сохраняемые сетевые настройки локального агента без ключа доступа</summary>
public sealed record AgentSettings
{
    public string LocalModelPath { get; init; } = "";
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5100;
    public bool AllowLan { get; init; }
    public bool UseTls { get; init; }
    public bool AllowRemoteControl { get; init; }
    public string CertificateFingerprint { get; init; } = "";
}

/// <summary>Размер обычного окна в логических единицах и положение на экране в пикселях</summary>
public sealed record WindowGeometry
{
    public double Width { get; init; } = 1240;
    public double Height { get; init; } = 880;
    public int? X { get; init; }
    public int? Y { get; init; }
    public bool IsMaximized { get; init; }
}

/// <summary>Адреса управления и вычислительного RPC одного узла</summary>
public sealed record NodeEndpoint
{
    public string RpcListenAddress { get; init; } = "0.0.0.0";
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int AgentPort { get; init; } = 5100;
    public int RpcPort { get; init; } = 50052;
    public bool Enabled { get; init; } = true;
    public bool UseTls { get; init; }
    public string CertificateFingerprint { get; init; } = "";

    public string RpcEndpoint => $"{(Host.Contains(':') ? $"[{Host}]" : Host)}:{RpcPort}";
}

/// <summary>Результат предварительной проверки с ключом локализованного сообщения</summary>
public sealed record Diagnostic(string Code, bool IsError, string Detail = "");

/// <summary>Подготовленная команда без исполнения через оболочку</summary>
public sealed record LaunchPlan(string Executable, string[] Arguments)
{
    /// <summary>Возвращает представление для просмотра, а не скрипт конкретной оболочки</summary>
    public string ToDisplayString() => string.Join(Environment.NewLine, new[] { Quote(Executable) }.Concat(Arguments.Select(Quote)));

    // Экранирует значения только для визуального представления
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
