namespace OpenLLMCluster.Infrastructure;

/// <summary>Наблюдаемый этап обработки; генерация не разделяется на скрытые рассуждения и финальный текст</summary>
public enum ModelActivityKind { Unavailable, Idle, Prompt, Generating, ContextShift }

/// <summary>Состояние всех активных слотов без содержимого пользовательских запросов</summary>
public sealed record ModelActivitySnapshot(ModelActivityKind Kind, int ActiveSlots = 0, int PromptTokens = 0, int DecodedTokens = 0);

public sealed partial class EngineApiClient
{
    /// <summary>Читает доступную диагностику слотов с коротким таймаутом; старый сервер может её не предоставлять</summary>
    public async Task<ModelActivitySnapshot> ReadActivityAsync(Uri baseUri, string apiKey, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var request = Request(HttpMethod.Get, new Uri(baseUri, "slots"), apiKey);
            using var response = await _client.SendAsync(request, timeout.Token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented) return new(ModelActivityKind.Unavailable);
            response.EnsureSuccessStatusCode();
            return ParseActivity(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Trace.TraceWarning("Активность модели: {0}", ex.GetType().Name); return new(ModelActivityKind.Unavailable); }
    }

    /// <summary>Разбирает фактические счётчики /slots без вывода prompt или предположений о reasoning</summary>
    public static ModelActivitySnapshot ParseActivity(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return new(ModelActivityKind.Unavailable);
        var active = 0;
        var prompt = 0;
        var decoded = 0;
        foreach (var slot in document.RootElement.EnumerateArray())
        {
            if (slot.ValueKind != JsonValueKind.Object || !slot.TryGetProperty("is_processing", out var processing)
                || processing.ValueKind != JsonValueKind.True) continue;
            active++;
            prompt += ReadCount(slot, "n_prompt_tokens");
            if (slot.TryGetProperty("next_token", out var next))
            {
                if (next.ValueKind == JsonValueKind.Array)
                    foreach (var token in next.EnumerateArray()) decoded += ReadCount(token, "n_decoded");
                else if (next.ValueKind == JsonValueKind.Object) decoded += ReadCount(next, "n_decoded");
            }
        }
        return new(active == 0 ? ModelActivityKind.Idle : decoded > 0 ? ModelActivityKind.Generating : ModelActivityKind.Prompt, active, prompt, decoded);
    }

    // Не подменяет отсутствующий счётчик содержимым запроса
    private static int ReadCount(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number
        && count.TryGetInt32(out var number) && number >= 0 ? number : 0;
}
