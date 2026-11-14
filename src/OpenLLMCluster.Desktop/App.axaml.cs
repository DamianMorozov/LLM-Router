namespace OpenLLMCluster.Desktop;

/// <summary>Связывает оболочку, модель представления и оформление</summary>
public partial class App : Application
{
    public ApplicationUpdateService? Updates { get; private set; }
    /// <summary>Загружает ресурсы оформления</summary>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Создаёт главное окно настольного приложения</summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Updates = new ApplicationUpdateService();
            desktop.MainWindow = CreateMainWindow(new ProfileStore());
            if (desktop.MainWindow.DataContext is MainWindowViewModel viewModel)
                viewModel.Events.Add($"{viewModel.Text[Updates.Status.MessageKey]} {Updates.ReleasesUrl}");
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Создаёт окно с рабочими привязками языка, профиля и темы</summary>
    public MainWindow CreateMainWindow(ProfileStore store, LocalAgentHost? agentHost = null)
    {
        var viewModel = new MainWindowViewModel(store, agentHost, updateService: Updates);
        viewModel.Initialize();
        ApplyTheme(viewModel.ThemeIndex);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.ThemeIndex)) ApplyTheme(viewModel.ThemeIndex);
        };
        var window = new MainWindow { DataContext = viewModel };
        window.ConfigureSettings(viewModel);
        window.Opened += (_, _) => viewModel.StartMonitoring();
        return window;
    }

    /// <summary>Применяет светлую, тёмную или системную тему</summary>
    public void ApplyTheme(int index) => RequestedThemeVariant = index switch { 1 => ThemeVariant.Light, 2 => ThemeVariant.Dark, _ => ThemeVariant.Default };
}
