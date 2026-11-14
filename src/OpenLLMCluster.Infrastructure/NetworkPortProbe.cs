namespace OpenLLMCluster.Infrastructure;

/// <summary>Результат TCP-проверки; не подтверждает авторизацию и работу MagicOnion</summary>
public sealed record PortProbeResult(bool IsOpen, string MessageKey);

/// <summary>Проверяет TCP-порты и перечисляет локальные адреса без изменения сетевых настроек</summary>
public sealed class NetworkPortProbe
{
    /// <summary>Устанавливает TCP-соединение с ограничением времени</summary>
    public async Task<PortProbeResult> CheckAsync(string host, int port, CancellationToken ct = default)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
            throw new ArgumentException("Укажи имя компьютера или IP и порт от 1 до 65535");
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, timeout.Token);
            return new(true, "TcpPortOpen");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "TcpPortTimeout"); }
        catch (SocketException ex) { Trace.TraceInformation("TCP probe {0}:{1}: {2}", host, port, ex.SocketErrorCode); return new(false, "TcpPortClosed"); }
    }

    /// <summary>Возвращает адрес для локальной проверки выбранного интерфейса</summary>
    public static string LocalProbeAddress(string listenAddress) => listenAddress switch
    {
        "0.0.0.0" => "127.0.0.1", "::" => "::1", _ => listenAddress
    };

    /// <summary>Перечисляет IPv4-адреса активных сетевых интерфейсов для подключения с другого ПК</summary>
    public static string GetLocalAddresses()
    {
        try
        {
            return string.Join(", ", NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up)
                .SelectMany(item => item.GetIPProperties().UnicastAddresses).Select(item => item.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address)).Distinct());
        }
        catch (Exception ex) { Trace.TraceWarning("Local addresses: {0}", ex.Message); return ""; }
    }
}
