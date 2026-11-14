namespace OpenLLMCluster.Infrastructure;

/// <summary>Создаёт или обновляет собственное правило для порта агента в частной локальной сети</summary>
public sealed class WindowsFirewall
{
    /// <summary>Формирует ограниченное правило; пользовательские строки в PowerShell не подставляются</summary>
    public static string BuildRuleScript(int port, bool rpc = false)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Недопустимый TCP-порт");
        var service = rpc ? "RPC" : "Agent";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $name = 'Open-LLM-Cluster-{{service}}-TCP-{{port}}'
            $rule = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
            $options = @{ Direction = 'Inbound'; Action = 'Allow'; Enabled = 'True'; Profile = 'Private' }
            if ($null -eq $rule) {
                New-NetFirewallRule -Name $name -DisplayName 'Open LLM Cluster {{service}} TCP {{port}}' @options -Protocol TCP `
                    -LocalPort {{port}} -RemoteAddress LocalSubnet | Out-Null
            } else {
                $rule | Set-NetFirewallRule @options
                $rule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort {{port}}
                $rule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress LocalSubnet
            }
            """;
    }

    /// <summary>Запрашивает UAC только по нажатию кнопки; отмена не меняет настройки приложения</summary>
    public Task EnsureRuleAsync(int port, CancellationToken ct = default) => EnsureCoreRuleAsync(port, rpc: false, ct);

    /// <summary>Открывает только вычислительный RPC-порт в частной локальной сети</summary>
    public Task EnsureRpcRuleAsync(int port, CancellationToken ct = default) => EnsureCoreRuleAsync(port, rpc: true, ct);

    // Запрашивает системное изменение правила только после команды пользователя
    private static async Task EnsureCoreRuleAsync(int port, bool rpc, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Кнопка брандмауэра поддерживается только в Windows");
        try
        {
            var script = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildRuleScript(port, rpc)));
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + script
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить настройку брандмауэра");
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new InvalidOperationException("Брандмауэр не подтвердил создание правила; код: " + process.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Запрос UAC отменён", ex, ct); }
        catch (Exception ex) { Trace.TraceError("Configure firewall: {0}", ex.GetType().Name); throw; }
    }
}
