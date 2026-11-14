namespace OpenLLMCluster.Infrastructure;

/// <summary>Физическая память системы; доступная память включает освобождаемый файловый кэш</summary>
public sealed record PhysicalMemorySample(long TotalBytes, long AvailableBytes);

/// <summary>Измеряет физическую RAM отдельно от бюджета сборщика мусора .NET</summary>
public sealed class MemoryLoadProbe
{
    /// <summary>Возвращает снимок ОС или отсутствие измерения, сохраняя отмену запроса</summary>
    public async Task<PhysicalMemorySample?> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (!GlobalMemoryStatusEx(ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return Validate(checked((long)status.TotalPhysical), checked((long)status.AvailablePhysical));
            }
            if (OperatingSystem.IsLinux()) return ParseLinux(await File.ReadAllTextAsync("/proc/meminfo", ct));
            if (OperatingSystem.IsMacOS())
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var runner = new EngineCommandRunner();
                var total = runner.RunAsync(new("/usr/sbin/sysctl", ["-n", "hw.memsize"]), timeout.Token);
                var pages = runner.RunAsync(new("/usr/bin/vm_stat", []), timeout.Token);
                await Task.WhenAll(total, pages);
                return ParseMac(await total, await pages);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Trace.TraceWarning("Измерение RAM: {0}", ex.GetType().Name); }
        return null;
    }

    /// <summary>Использует MemAvailable, а не только свободные страницы Linux</summary>
    public static PhysicalMemorySample? ParseLinux(string text)
    {
        var total = ReadLinuxValue(text, "MemTotal");
        var available = ReadLinuxValue(text, "MemAvailable");
        return total is { } t && available is { } a ? Validate(t, a) : null;
    }

    /// <summary>Оценивает доступную RAM macOS по свободным, неактивным и спекулятивным страницам</summary>
    public static PhysicalMemorySample? ParseMac(string totalText, string pagesText)
    {
        if (!long.TryParse(totalText.Trim(), out var total)) return null;
        var first = pagesText.Split('\n')[0];
        var marker = first.IndexOf("page size of ", StringComparison.Ordinal);
        if (marker < 0 || !long.TryParse(first[(marker + 13)..].Split(' ')[0], out var pageSize) || pageSize <= 0) return null;
        long pages = 0;
        foreach (var name in new[] { "Pages free", "Pages inactive", "Pages speculative" })
        {
            var line = pagesText.Split('\n').FirstOrDefault(value => value.StartsWith(name + ":", StringComparison.Ordinal));
            if (line is null || !long.TryParse(line[(line.IndexOf(':') + 1)..].Trim().TrimEnd('.'), out var count) || count < 0) return null;
            if (count > long.MaxValue - pages) return null;
            pages += count;
        }
        return pages <= long.MaxValue / pageSize ? Validate(total, pages * pageSize) : null;
    }

    // Отсутствующее или некорректное значение не превращает RAM в фиктивный ноль
    private static long? ReadLinuxValue(string text, string name)
    {
        var line = text.Split('\n').FirstOrDefault(value => value.StartsWith(name + ":", StringComparison.Ordinal));
        var parts = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: 3 } && parts[2] == "kB" && long.TryParse(parts[1], out var value) && value >= 0
            && value <= long.MaxValue / 1024 ? value * 1024 : null;
    }

    // Отклоняет несовместимые показатели одного снимка ОС
    private static PhysicalMemorySample? Validate(long total, long available) => total > 0 && available >= 0 && available <= total
        ? new(total, available) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
