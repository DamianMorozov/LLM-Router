namespace OpenLLMCluster.Infrastructure;

/// <summary>Измеряет общую загрузку CPU по разнице системных счётчиков, не по загрузке процесса Desktop</summary>
public sealed class CpuLoadProbe
{
    private readonly object _sync = new();
    private (ulong Idle, ulong Total)? _previous;

    /// <summary>Возвращает процент с предыдущего измерения; первый снимок Windows/Linux ещё не содержит интервала</summary>
    public async Task<double?> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) return await ReadMacAsync(ct);
            (ulong Idle, ulong Total) current;
            if (OperatingSystem.IsWindows())
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new Win32Exception(Marshal.GetLastWin32Error());
                current = (idle, kernel + user);
            }
            else if (OperatingSystem.IsLinux()) current = ParseLinux(await File.ReadAllTextAsync("/proc/stat", ct));
            else return null;
            lock (_sync)
            {
                var previous = _previous;
                _previous = current;
                if (previous is null || current.Total <= previous.Value.Total || current.Idle < previous.Value.Idle) return null;
                return Calculate(previous.Value.Idle, previous.Value.Total, current.Idle, current.Total);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Trace.TraceWarning("Измерение CPU: {0}", ex.GetType().Name); return null; }
    }

    /// <summary>Вычисляет занятость CPU, включая реальный ноль, но отклоняя сброс счётчиков</summary>
    public static double? Calculate(ulong oldIdle, ulong oldTotal, ulong idle, ulong total) => total <= oldTotal || idle < oldIdle
        ? null : Math.Clamp(100d * (1 - (double)(idle - oldIdle) / (total - oldTotal)), 0, 100);

    /// <summary>Разбирает агрегат /proc/stat; guest уже входит в user/nice и повторно не суммируется</summary>
    public static (ulong Idle, ulong Total) ParseLinux(string text)
    {
        var fields = text.Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || fields[0] != "cpu") throw new InvalidDataException("Не распознан /proc/stat");
        var values = fields.Skip(1).Take(8).Select(ulong.Parse).ToArray();
        return (values[3] + (values.Length > 4 ? values[4] : 0), values.Aggregate(0UL, (sum, value) => sum + value));
    }

    // Системная утилита macOS показывает второй интервальный снимок вместо среднего с запуска ОС
    private static async Task<double?> ReadMacAsync(CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("/usr/bin/top")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in new[] { "-l", "2", "-s", "1", "-n", "0" }) process.StartInfo.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var line = (await output).Split('\n').LastOrDefault(value => value.StartsWith("CPU usage:", StringComparison.Ordinal));
            if (process.ExitCode != 0 || line is null) { Trace.TraceWarning("CPU top: {0}", await error); return null; }
            var idle = line.Split(',').Last().Trim().Split('%')[0];
            return Math.Clamp(100 - double.Parse(idle, System.Globalization.CultureInfo.InvariantCulture), 0, 100);
        }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
