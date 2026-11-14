namespace OpenLLMCluster.Desktop.Services;

/// <summary>Разделяет обнаружение версии и последующие этапы скачивания и применения</summary>
public enum ApplicationUpdateState { NotInstalled, Idle, Checking, NoUpdate, Available, Downloading, ReadyToInstall, Applying, Error }

/// <summary>Содержит описание найденной версии без зависимости интерфейса от типов Velopack</summary>
public sealed record AvailableApplicationUpdate(string Version, string NotesMarkdown);

/// <summary>Предоставляет неизменяемый снимок состояния для интерфейса и журнала</summary>
public sealed record ApplicationUpdateStatus(ApplicationUpdateState State, string CurrentVersion, string Channel, string MessageKey,
    AvailableApplicationUpdate? Release = null, int Progress = 0);

/// <summary>Отделяет сервис состояний от определения установки и сетевого источника</summary>
public interface IApplicationUpdateAdapter
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    string Channel { get; }
    /// <summary>Проверяет наличие новой версии без скачивания пакетов и изменения установки</summary>
    Task<AvailableApplicationUpdate?> CheckAsync(CancellationToken ct = default);
    /// <summary>Скачивает и проверяет ранее найденный пакет, не применяя обновление</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken ct = default);
    /// <summary>Повторно проверяет пакет и наличие механизма применения перед остановкой приложения</summary>
    Task ValidateReadyAsync(CancellationToken ct = default);
    /// <summary>Передаёт проверенный пакет обновлятору и перезапускает приложение после подготовки</summary>
    void ApplyAndRestart();
}
