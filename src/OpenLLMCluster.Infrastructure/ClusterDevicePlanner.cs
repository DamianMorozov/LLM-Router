namespace OpenLLMCluster.Infrastructure;

/// <summary>Устройство в порядке, подтверждённом текущим llama-server --list-devices</summary>
public sealed record ClusterDevice(string Id, string Description, long TotalMiB, long FreeMiB);

/// <summary>Предложение пропорций по свободной памяти устройств; это не оценка вместимости модели и KV-cache</summary>
public sealed record DeviceSplitSuggestion(string Devices, string TensorSplit);

/// <summary>Перечисляет настоящие устройства кластера и предлагает проверяемое распределение</summary>
public sealed class ClusterDevicePlanner(EngineCommandRunner? runner = null)
{
    /// <summary>Сверяет локальный build, затем выполняет реальный обмен RPC при перечислении устройств</summary>
    public async Task<IReadOnlyList<ClusterDevice>> DiscoverAsync(RouterProfile profile, CancellationToken ct = default)
    {
        try
        {
            var command = runner ?? new();
            var version = await command.ValidateBuildAsync(profile.RuntimeDirectory, ct);
            LlamaPackageInstaller.RequireBuildVersion(version, profile.LlamaReleaseTag);
            var args = new List<string>();
            var nodes = profile.Nodes.Where(node => node.Enabled).ToArray();
            if (nodes.Any(node => !RuntimePlanner.IsValidNode(node))) throw new ArgumentException("Недопустимый адрес узла");
            if (nodes.Length > 0) args.AddRange(["--rpc", string.Join(',', nodes.Select(node => node.RpcEndpoint))]);
            args.Add("--list-devices");
            var output = await command.RunAsync(new(Path.Combine(profile.RuntimeDirectory, RuntimePlanner.GetExecutableName(NodeRole.Coordinator)),
                args.ToArray()), ct);
            var devices = Parse(output);
            foreach (var node in nodes)
                if (!devices.Any(device => device.Description.Contains(node.RpcEndpoint, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("RPC не подтвердил устройство узла " + node.Name);
            return devices;
        }
        catch (Exception ex) { Trace.TraceError("Устройства кластера: {0}", ex.GetType().Name); throw; }
    }

    /// <summary>Разбирает стандартные строки CUDA/Metal/RPC с памятью в MiB; строки журнала не становятся устройствами</summary>
    public static IReadOnlyList<ClusterDevice> Parse(string output)
    {
        var result = new List<ClusterDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            var memory = line.LastIndexOf(" (", StringComparison.Ordinal);
            if (separator <= 0 || memory <= separator || !line.EndsWith(" MiB free)", StringComparison.Ordinal)) continue;
            var id = line[..separator];
            if (id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.')) continue;
            var parts = line[(memory + 2)..].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !long.TryParse(parts[0].Split(' ')[0], out var total)
                || !long.TryParse(parts[1].Split(' ')[0], out var free) || total <= 0 || free < 0 || free > total) continue;
            if (result.Any(device => device.Id == id)) throw new InvalidDataException("Повторный идентификатор устройства в CLI");
            result.Add(new(id, line[(separator + 2)..memory], total, free));
        }
        return result;
    }

    /// <summary>Вычитает резерв из каждого устройства и сохраняет порядок явно выбранных устройств</summary>
    public static DeviceSplitSuggestion Suggest(IReadOnlyList<ClusterDevice> devices, int reserveMiB = 1024)
    {
        if (reserveMiB < 0) throw new ArgumentOutOfRangeException(nameof(reserveMiB));
        var usable = devices.Where(device => device.FreeMiB > reserveMiB).ToArray();
        if (usable.Length == 0) throw new InvalidOperationException("Нет устройств со свободной памятью сверх резерва");
        return new(string.Join(',', usable.Select(device => device.Id)),
            string.Join(',', usable.Select(device => (device.FreeMiB - reserveMiB).ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }
}
