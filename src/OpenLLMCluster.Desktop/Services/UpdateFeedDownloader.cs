namespace OpenLLMCluster.Desktop.Services;

/// <summary>Добавляет настоящую отмену HTTP-проверки к API метаданных Velopack без CancellationToken</summary>
public sealed class UpdateFeedDownloader(CancellationToken ct) : HttpClientFileDownloader
{
    /// <summary>Получает индекс канала с ограничением времени одного запроса в тридцать секунд</summary>
    public override async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
    {
        try
        {
            using var client = CreateHttpClient(headers, Math.Min(timeout, 0.5));
            return await client.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) { Trace.TraceError("Update index request: {0}", ex.GetType().Name); throw; }
    }

    /// <summary>Получает список GitHub Releases с тем же токеном отмены, что и проверка</summary>
    public override async Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
    {
        try
        {
            using var client = CreateHttpClient(headers, Math.Min(timeout, 0.5));
            return await client.GetStringAsync(url, ct);
        }
        catch (Exception ex) { Trace.TraceError("Update releases request: {0}", ex.GetType().Name); throw; }
    }
}
