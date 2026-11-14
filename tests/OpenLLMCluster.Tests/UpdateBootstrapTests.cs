// Проверяет ранний выход настоящего Desktop по служебному аргументу Velopack до запуска интерфейса
namespace OpenLLMCluster.Tests;

public sealed class UpdateBootstrapTests
{
    [Fact]
    public async Task InstallHookExitsSuccessfullyBeforeOpeningDesktop()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Open-LLM-Cluster.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = OperatingSystem.IsWindows() ? "OpenLLMCluster.Desktop.exe" : "OpenLLMCluster.Desktop";
        var path = Path.Combine(root.FullName, "src", "OpenLLMCluster.Desktop", "bin", configuration, "net10.0", executable);
        Assert.True(File.Exists(path), "Нужен результат обычной сборки Desktop");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--veloapp-install");
        start.ArgumentList.Add(ApplicationInfo.Version);
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var exited = false;
        try { await process.WaitForExitAsync(timeout.Token); exited = true; }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested) { }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(TestContext.Current.CancellationToken); } }
        Assert.True(exited, "Служебный hook должен завершаться до создания окна Avalonia");
        Assert.Equal(0, process.ExitCode);
    }
}
