// Проверяет реальные привязки карточки обновлений и отсутствие автоматических действий
namespace OpenLLMCluster.Tests;

public sealed class UpdateInterfaceTests
{
    [AvaloniaFact]
    public void DedicatedUpdateTabExposesActionsAndKeepsJournalLast()
    {
        var store = new ProfileStore(Path.Combine(Path.GetTempPath(), "olc-ui-" + Guid.NewGuid().ToString("N"), "profile.json"));
        var window = new MainWindow { DataContext = new MainWindowViewModel(store) };
        window.Show();
        try
        {
            var tabs = window.FindControl<TabControl>("WorkspaceTabs")!;
            var updateTab = window.FindControl<TabItem>("UpdatesTab");
            Assert.NotNull(updateTab);
            Assert.Same(window.FindControl<TabItem>("JournalTab"), tabs.Items[tabs.Items.Count - 1]);
            var overview = (Control)window.FindControl<TabItem>("OverviewTab")!.Content!;
            Assert.DoesNotContain(overview.GetVisualDescendants(), control => control.Name == "ApplicationUpdateCard");
            SelectUpdateTab(window);
            Assert.NotNull(window.FindControl<Border>("ApplicationUpdateCard"));
            Assert.Contains(window.FindControl<Border>("ApplicationUpdateCard")!, ((Control)updateTab.Content!).GetVisualDescendants());
            Assert.NotNull(window.FindControl<Button>("CheckUpdatesButton")!.Command);
            Assert.NotNull(window.FindControl<Button>("DownloadUpdateButton")!.Command);
            Assert.NotNull(window.FindControl<Button>("InstallUpdateButton")!.Command);
            Assert.NotNull(window.FindControl<SelectableTextBlock>("UpdateReleaseNotes"));
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.LanguageIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Обновление", updateTab.Header);
            vm.LanguageIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Update", updateTab.Header);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ButtonsDriveCheckDownloadProgressCancellationRetryAndConfirmation()
    {
        var adapter = new UpdateTestAdapter { DownloadWait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new ApplicationUpdateService(() => adapter);
        using var vm = CreateViewModel(service);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        SelectUpdateTab(window);
        try
        {
            Assert.Equal(0, adapter.Checks);
            Assert.Equal(0, adapter.Downloads);
            Assert.False(window.FindControl<Button>("DownloadUpdateButton")!.IsEffectivelyEnabled);
            await ExecuteAsync(window, "CheckUpdatesButton");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("0.2.301", window.FindControl<SelectableTextBlock>("UpdateAvailableVersion")!.Text);
            Assert.Equal(adapter.Release!.NotesMarkdown, window.FindControl<SelectableTextBlock>("UpdateReleaseNotes")!.Text);
            Assert.False(window.FindControl<Button>("InstallUpdateButton")!.IsEffectivelyEnabled);
            var pending = ExecuteAsync(window, "DownloadUpdateButton");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(40, window.FindControl<ProgressBar>("UpdateDownloadProgress")!.Value);
            Assert.False(window.FindControl<Button>("CheckUpdatesButton")!.IsEffectivelyEnabled);
            Assert.True(window.FindControl<Button>("CancelUpdateButton")!.IsEffectivelyEnabled);
            window.FindControl<Button>("CancelUpdateButton")!.Command!.Execute(null);
            await pending;
            Dispatcher.UIThread.RunJobs();
            Assert.True(adapter.DownloadCancelled);
            Assert.True(window.FindControl<Button>("DownloadUpdateButton")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<Button>("InstallUpdateButton")!.IsEffectivelyEnabled);
            adapter.DownloadWait = null;
            adapter.DownloadFailure = new HttpRequestException("offline");
            await ExecuteAsync(window, "DownloadUpdateButton");
            Assert.Equal(ApplicationUpdateState.Error, service.Status.State);
            adapter.DownloadFailure = null;
            await ExecuteAsync(window, "DownloadUpdateButton");
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Button>("InstallUpdateButton")!.IsEffectivelyEnabled);
            Assert.False(vm.Updates.IsConfirmationVisible);
            window.FindControl<Button>("InstallUpdateButton")!.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Border>("UpdateConfirmation")!.IsVisible);
            Assert.True(window.FindControl<Button>("ConfirmUpdateButton")!.IsEffectivelyEnabled);
            vm.LanguageIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Install and restart", window.FindControl<Button>("InstallUpdateButton")!.Content);
            Assert.Equal(vm.Text["UpdateReady"], window.FindControl<TextBlock>("UpdateStatusText")!.Text);
            vm.LanguageIndex = 1;
            vm.IsLocalModelBusy = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(vm.Text["UpdateConfirmBusy"], window.FindControl<TextBlock>("UpdateConfirmationText")!.Text);
            CaptureCard(window, "confirmation");
            window.FindControl<Button>("DismissUpdateButton")!.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.Updates.IsConfirmationVisible);
            Assert.True(vm.IsLocalModelBusy);
            Assert.Equal(ApplicationUpdateState.ReadyToInstall, service.Status.State);
            CaptureCard(window, "ready");
        }
        finally { vm.IsLocalModelBusy = false; window.Close(); }
    }

    [AvaloniaFact]
    public void UnpackagedAppOffersReleasesAndDisablesUpdateOperations()
    {
        var adapter = new UpdateTestAdapter { IsInstalled = false };
        using var vm = CreateViewModel(new ApplicationUpdateService(() => adapter));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        SelectUpdateTab(window);
        try
        {
            Assert.False(window.FindControl<Button>("CheckUpdatesButton")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<Button>("DownloadUpdateButton")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<Button>("InstallUpdateButton")!.IsEffectivelyEnabled);
            Assert.True(window.FindControl<Button>("OpenReleasesButton")!.IsEffectivelyEnabled);
            Assert.Equal(vm.Text["UpdateNotInstalled"], window.FindControl<TextBlock>("UpdateStatusText")!.Text);
            Assert.Equal(0, adapter.Checks);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingWindowCancelsDownloadAndDetachesLocalizedBindings()
    {
        var adapter = new UpdateTestAdapter { DownloadWait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var vm = CreateViewModel(new ApplicationUpdateService(() => adapter));
        await vm.Updates.CheckCommand.ExecuteAsync(null);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        SelectUpdateTab(window);
        var pending = ExecuteAsync(window, "DownloadUpdateButton");
        window.Close();
        await pending;
        Assert.True(adapter.DownloadCancelled);
        Assert.False(vm.Updates.CanDownload);
        Assert.False(vm.Updates.CanCheck);
        var changes = 0;
        vm.Updates.PropertyChanged += (_, _) => changes++;
        vm.Text.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, changes);
    }

    [AvaloniaFact]
    public async Task CancelButtonAlsoCancelsVersionCheckAndAllowsRetry()
    {
        var adapter = new UpdateTestAdapter { CheckWait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var vm = CreateViewModel(new ApplicationUpdateService(() => adapter));
        var pending = vm.Updates.CheckCommand.ExecuteAsync(null);
        vm.Updates.CancelCommand.Execute(null);
        await pending;
        Assert.True(adapter.CheckCancelled);
        adapter.CheckWait = null;
        await vm.Updates.CheckCommand.ExecuteAsync(null);
        Assert.Equal("0.2.301", vm.Updates.AvailableVersion);
    }

    [AvaloniaFact]
    public async Task InstallationCoordinatorRequiresSeparateExplicitConfirmation()
    {
        var calls = 0;
        var busy = false;
        using var updates = new ApplicationUpdatesViewModel(new ApplicationUpdateService(() => new UpdateTestAdapter()),
            new Localizer(), () => busy, install: _ => { calls++; return Task.CompletedTask; });
        await updates.CheckCommand.ExecuteAsync(null);
        await updates.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(0, calls);
        updates.RequestInstallCommand.Execute(null);
        Assert.Equal(0, calls);
        updates.DismissConfirmationCommand.Execute(null);
        Assert.False(updates.ConfirmInstallCommand.CanExecute(null));
        updates.RequestInstallCommand.Execute(null);
        busy = true;
        updates.Refresh();
        Assert.Equal(updates.Text["UpdateConfirmBusy"], updates.ConfirmationText);
        await updates.ConfirmInstallCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void UpdateLabelsAndMessagesHaveBothTranslations()
    {
        var assembly = typeof(App).Assembly;
        using var ru = assembly.GetManifestResourceStream("OpenLLMCluster.Desktop.Localization.ru.json")!;
        using var en = assembly.GetManifestResourceStream("OpenLLMCluster.Desktop.Localization.en.json")!;
        var russian = JsonSerializer.Deserialize<Dictionary<string, string>>(ru)!;
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(en)!;
        var keys = russian.Keys.Concat(english.Keys).Where(key => key.StartsWith("Update", StringComparison.Ordinal)).Distinct();
        foreach (var key in keys)
        {
            Assert.True(russian.TryGetValue(key, out var ruText) && !string.IsNullOrWhiteSpace(ruText), key);
            Assert.True(english.TryGetValue(key, out var enText) && !string.IsNullOrWhiteSpace(enText), key);
        }
    }

    // Открывает содержимое вкладки перед проверкой визуальных привязок
    private static void SelectUpdateTab(MainWindow window)
    {
        var tab = window.FindControl<TabItem>("UpdatesTab");
        Assert.NotNull(tab);
        window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = tab;
        Dispatcher.UIThread.RunJobs();
    }

    // Создаёт изолированное окно без загрузки пользовательского профиля
    private static MainWindowViewModel CreateViewModel(ApplicationUpdateService service)
    {
        var store = new ProfileStore(Path.Combine(Path.GetTempPath(), "olc-ui-" + Guid.NewGuid().ToString("N"), "profile.json"));
        return new MainWindowViewModel(store, updateService: service);
    }

    // Выполняет именно команду XAML-кнопки, а не обходной вызов сервиса
    private static Task ExecuteAsync(MainWindow window, string name) =>
        ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)window.FindControl<Button>(name)!.Command!).ExecuteAsync(null);

    // Сохраняет кадр карточки для визуальной проверки вне репозитория
    private static void CaptureCard(MainWindow window, string state)
    {
        var destination = Environment.GetEnvironmentVariable("OLC_UPDATE_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(destination)) return;
        Directory.CreateDirectory(destination);
        var vm = (MainWindowViewModel)window.DataContext!;
        foreach (var language in new[] { 1, 2 })
        {
            vm.LanguageIndex = language;
            ((App)Application.Current!).ApplyTheme(2);
            window.FindControl<Expander>("UpdateNotesExpander")!.IsExpanded = true;
            window.UpdateLayout();
            window.FindControl<Border>("ApplicationUpdateCard")!.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame()!;
            frame.Save(Path.Combine(destination, $"updates-{state}-{(language == 1 ? "ru" : "en")}-dark.png"),
                Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
