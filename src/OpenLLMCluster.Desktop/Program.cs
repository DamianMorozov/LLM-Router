namespace OpenLLMCluster.Desktop;

/// <summary>Точка входа кроссплатформенного приложения</summary>
public static class Program
{
    /// <summary>Инициализирует платформу и запускает цикл обработки событий</summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // Загруженный пакет применяется только после явного подтверждения пользователя
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(autoApply: false).Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Создаёт приложение для обычного запуска и тестов интерфейса</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
