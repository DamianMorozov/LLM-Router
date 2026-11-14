// Проверяет отсутствие runtime через реальное завершение дочернего процесса и появление действия восстановления
namespace OpenLLMCluster.Tests;

public sealed class RuntimeRecoveryTests
{
    public static bool IsNonWindows => !OperatingSystem.IsWindows();

    [AvaloniaTheory(SkipWhen = nameof(IsNonWindows), Skip = "Воспроизведение запуска Windows apphost")]
    [InlineData("success", "MissingAspNetRuntime.cmd")]
    [InlineData("cancel", "MissingAspNetRuntime.cmd")]
    [InlineData("failure", "MissingAspNetRuntime.cmd")]
    [InlineData("success", "OutdatedNetCoreRuntime.cmd")]
    [InlineData("cancel", "OutdatedNetCoreRuntime.cmd")]
    [InlineData("failure", "OutdatedNetCoreRuntime.cmd")]
    public async Task MissingAspNetRuntimeShowsFriendlyStatusAndInstallButton(string outcome, string fixture)
    {
        using var host = new LocalAgentHost(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        var installer = new StubInstaller(outcome);
        var store = new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profile.json"));
        var vm = new MainWindowViewModel(store, host, installer);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("LocalAgentTab");
            await vm.StartLocalAgentCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("You must install", vm.LocalAgentStatus);
            Assert.Contains("ASP.NET Core Runtime", vm.LocalAgentStatus);
            var button = window.FindControl<Button>("InstallAspNetRuntimeButton");
            Assert.NotNull(button);
            Assert.True(button.IsEffectivelyVisible);
            Assert.NotNull(button.Command);
            Assert.True(button.Command.CanExecute(null));
            Assert.False(host.IsRunning);
            var command = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(button.Command);
            await command.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, installer.Calls);
            Assert.Equal("x64", installer.Architecture);
            Assert.False(vm.IsLocalAgentBusy);
            Assert.False(host.IsRunning);
            if (outcome == "success")
            {
                Assert.False(button.IsEffectivelyVisible);
                Assert.StartsWith(vm.Text["RuntimeInstalledRetry"], vm.LocalAgentStatus);
                await vm.StartLocalAgentCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                Assert.True(button.IsEffectivelyVisible);
                Assert.True(button.Command.CanExecute(null));
            }
            else
            {
                Assert.True(button.IsEffectivelyVisible);
                Assert.True(button.Command.CanExecute(null));
                Assert.StartsWith(vm.Text[outcome == "cancel" ? "RuntimeInstallCancelled" : "RuntimeInstallFailed"], vm.LocalAgentStatus);
            }
            var directory = Path.Combine(Path.GetTempPath(), "OpenLLMCluster.Tests", "screenshots");
            Directory.CreateDirectory(directory);
            foreach (var language in new[] { 1, 2 })
            {
                vm.LanguageIndex = language;
                window.FindControl<ScrollViewer>("LocalAgentScroller")!.Offset = new Vector(0, 450);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame()!;
                frame.Save(Path.Combine(directory, $"runtime-recovery-{(language == 1 ? "ru" : "en")}.png"),
                    Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    private sealed class StubInstaller(string outcome) : IAspNetRuntimeInstaller
    {
        public int Calls { get; private set; }
        public string Architecture { get; private set; } = "";

        public Task<RuntimeInstallResult> InstallAsync(string architecture, IProgress<RuntimeInstallProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            Architecture = architecture;
            return outcome switch
            {
                "success" => Task.FromResult(new RuntimeInstallResult("10.0.15", RestartRequired: false)),
                "cancel" => Task.FromException<RuntimeInstallResult>(new OperationCanceledException()),
                _ => Task.FromException<RuntimeInstallResult>(new HttpRequestException("Test network error"))
            };
        }
    }
}
