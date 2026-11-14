namespace OpenLLMCluster.Desktop.Views;

/// <summary>Представление, геометрия окна и платформенные диалоги выбора файлов</summary>
public partial class MainWindow : Window
{
    private MainWindowViewModel? _settings;
    private WindowGeometry _normalBounds = new();
    private bool _lastMaximized;

    /// <summary>Загружает XAML главного окна</summary>
    public MainWindow()
    {
        InitializeComponent();
        PositionChanged += (_, _) => CaptureNormalBounds();
        Resized += (_, _) => CaptureNormalBounds();
        Opened += (_, _) => CaptureNormalBounds();
    }

    /// <summary>Восстанавливает геометрию до показа и подключает сохранение к закрытию</summary>
    public void ConfigureSettings(MainWindowViewModel viewModel)
    {
        _settings = viewModel;
        var saved = viewModel.WindowGeometry;
        Width = double.IsFinite(saved.Width) ? Math.Max(MinWidth, saved.Width) : 1240;
        Height = double.IsFinite(saved.Height) ? Math.Max(MinHeight, saved.Height) : 880;
        if (saved.X is int x && saved.Y is int y)
        {
            var position = new PixelPoint(x, y);
            var screen = Screens.ScreenFromPoint(position) ?? Screens.Primary;
            if (screen is not null)
            {
                var area = screen.WorkingArea;
                Width = Math.Min(Width, Math.Max(MinWidth, area.Width / screen.Scaling));
                Height = Math.Min(Height, Math.Max(MinHeight, area.Height / screen.Scaling));
                position = new PixelPoint(Math.Clamp(x, area.X, Math.Max(area.X, area.Right - (int)(Width * screen.Scaling))),
                    Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - (int)(Height * screen.Scaling))));
            }
            Position = position;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        _normalBounds = new WindowGeometry { Width = Width, Height = Height, X = Position.X, Y = Position.Y };
        _lastMaximized = saved.IsMaximized;
        if (saved.IsMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Подтверждает остановку модели и сохраняет настройки до уничтожения окна, кроме отменённого закрытия</summary>
    protected override void OnClosing(WindowClosingEventArgs args)
    {
        base.OnClosing(args);
        if (args.Cancel) return;
        if (DataContext is MainWindowViewModel vm && !vm.TryStopEngineForClose()) { args.Cancel = true; return; }
        if (_settings is null) return;
        CaptureNormalBounds();
        _settings.WindowGeometry = _normalBounds with { IsMaximized = WindowState == WindowState.Maximized
            || (WindowState == WindowState.Minimized && _lastMaximized) };
        _settings.SaveOnClose();
    }

    /// <summary>Останавливает принадлежащий окну агент после подтверждённого закрытия</summary>
    protected override void OnClosed(EventArgs args)
    {
        if (DataContext is MainWindowViewModel vm) vm.Dispose();
        base.OnClosed(args);
    }

    // Копирует ключ по явной команде пользователя без записи в профиль и журнал
    private async void CopyAgentToken_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel vm || Clipboard is null) return;
        try { await Clipboard.SetTextAsync(vm.LocalAgentToken); vm.LocalAgentStatus = vm.Text["AgentTokenCopied"]; }
        catch (Exception ex) { Trace.TraceError("Copy agent token: {0}", ex.GetType().Name); vm.LocalAgentStatus = vm.Text["OperationFailed"]; }
    }

    // Запоминает последнюю обычную геометрию без размеров свёрнутого или развёрнутого окна
    private void CaptureNormalBounds()
    {
        if (!IsVisible) return;
        if (WindowState != WindowState.Minimized) _lastMaximized = WindowState == WindowState.Maximized;
        if (WindowState != WindowState.Normal) return;
        _normalBounds = new WindowGeometry { Width = Width, Height = Height, X = Position.X, Y = Position.Y };
        if (_settings is not null) _settings.WindowGeometry = _normalBounds;
    }

    // Выбирает файл на самом узле; его путь не принимается от другого компьютера
    private async void BrowseLocalModel_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.CanConfigureLocalModel) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                { AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("GGUF") { Patterns = ["*.gguf"] }] });
            if (files.Count > 0) vm.LocalModelPath = files[0].TryGetLocalPath() ?? "";
        }
        catch (Exception ex) { Trace.TraceError("Локальная модель: {0}", ex); vm.LocalModelStatus = vm.Text["OperationFailed"]; }
    }

    // Открывает нативный диалог выбора каталога движка
    private async void BrowseRuntime_Click(object? sender, RoutedEventArgs args)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (folders.Count > 0 && DataContext is MainWindowViewModel vm) vm.RuntimeDirectory = folders[0].TryGetLocalPath() ?? "";
        }
        catch (Exception ex) { Trace.TraceError("Folder picker: {0}", ex); }
    }

    // Открывает нативный диалог выбора каталога моделей
    private async void BrowseModelDirectory_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (folders.Count > 0) vm.ModelDirectory = folders[0].TryGetLocalPath() ?? "";
        }
        catch (Exception ex) { Trace.TraceError("Диалог каталога моделей: {0}", ex); vm.Status = vm.Text["OperationFailed"]; }
    }

    // Открывает нативный диалог выбора GGUF в найденном или указанном каталоге моделей
    private async void BrowseModel_Click(object? sender, RoutedEventArgs args)
    {
        try
        {
            var vm = DataContext as MainWindowViewModel;
            var folder = vm is not null && !string.IsNullOrWhiteSpace(vm.ModelDirectory)
                ? await StorageProvider.TryGetFolderFromPathAsync(vm.ModelDirectory) : null;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false, SuggestedStartLocation = folder,
                FileTypeFilter = [new FilePickerFileType("GGUF") { Patterns = ["*.gguf"] }]
            });
            if (files.Count > 0 && vm is not null) vm.ModelPath = files[0].TryGetLocalPath() ?? "";
        }
        catch (Exception ex) { Trace.TraceError("File picker: {0}", ex); }
    }
}
