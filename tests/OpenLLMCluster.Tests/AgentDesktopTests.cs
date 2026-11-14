// Проверяет сетевые настройки через XAML, запуск настоящего агента кнопкой и остановку при закрытии окна
namespace OpenLLMCluster.Tests;

public sealed class AgentDesktopTests
{
    [AvaloniaFact]
    public async Task AgentStartsFromUiAndWindowCloseStopsItWithProtectedToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), "open-llm-cluster-agent-ui-" + Guid.NewGuid().ToString("N"));
        var store = new ProfileStore(Path.Combine(directory, "profile.json"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var host = new LocalAgentHost(typeof(Agent.Program).Assembly.Location);
        var app = (App)Application.Current!;
        var window = app.CreateMainWindow(store, host);
        window.Show();
        try
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            window.FindControl<TabControl>("WorkspaceTabs")!.SelectedItem = window.FindControl<TabItem>("LocalAgentTab");
            Dispatcher.UIThread.RunJobs();
            window.FindControl<ToggleSwitch>("LocalAgentLanSwitch")!.IsChecked = true;
            Assert.True(vm.AllowAgentLan);
            Assert.Equal("0.0.0.0", vm.LocalAgentListenAddress);
            vm.LocalAgentListenAddress = "127.0.0.1";
            vm.LocalAgentPort = port;
            Assert.Empty(vm.LocalAgentToken);
            var button = window.FindControl<Button>("StartLocalAgentButton")!;
            var start = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(button.Command);
            await start.ExecuteAsync(null);
            Assert.True(host.IsRunning, vm.LocalAgentStatus);
            Assert.True(vm.IsLocalAgentRunning);
            Assert.Equal(64, vm.LocalAgentToken.Length);
            Assert.False(vm.GenerateAgentTokenCommand.CanExecute(null));
            var token = vm.LocalAgentToken;
            vm.Nodes.Add(new(new() { Name = "local", Host = "127.0.0.1", AgentPort = port, RpcPort = port == 50052 ? 50053 : 50052 }, vm.Text));
            await vm.CheckNodeAgentPortCommand.ExecuteAsync(vm.Nodes[0]);
            Assert.StartsWith(vm.Text["TcpPortOpen"], vm.Nodes[0].Status);
            window.Close();
            Assert.False(host.IsRunning);
            Assert.False((await new NetworkPortProbe().CheckAsync("127.0.0.1", port, TestContext.Current.CancellationToken)).IsOpen);
            Assert.DoesNotContain(token, await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
            var reopened = app.CreateMainWindow(store);
            try
            {
                var restored = (MainWindowViewModel)reopened.DataContext!;
                Assert.True(restored.AllowAgentLan);
                Assert.Equal(port, restored.LocalAgentPort);
                Assert.Equal("127.0.0.1", restored.LocalAgentListenAddress);
                Assert.Equal(token, restored.LocalAgentToken);
                Assert.False(restored.IsLocalAgentRunning);
            }
            finally { ((MainWindowViewModel)reopened.DataContext!).Dispose(); }
        }
        finally { if (window.IsVisible) window.Close(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
