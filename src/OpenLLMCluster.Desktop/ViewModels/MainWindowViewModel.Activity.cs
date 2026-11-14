namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private ModelActivitySnapshot _modelActivity = new(ModelActivityKind.Unavailable);
    public string ModelActivityText => IsModelLoading ? ModelLoadStageText : !IsEngineRunning ? Text["ModelNotLoaded"]
        : Text[_modelActivity.Kind switch
        {
            ModelActivityKind.Idle => "ActivityIdle", ModelActivityKind.Prompt => "ActivityPrompt", ModelActivityKind.Generating => "ActivityGenerating",
            ModelActivityKind.ContextShift => "ActivityContextShift", _ => "ActivityUnavailable"
        }] + (_modelActivity.ActiveSlots > 0
            ? $" · {Text["ActivitySlots"]}: {_modelActivity.ActiveSlots} · {Text["ActivityInput"]}: {_modelActivity.PromptTokens:N0}"
                + $" · {Text["ActivityOutput"]}: {_modelActivity.DecodedTokens:N0}" : "");

    // Опрос не передаёт текст запросов и не удерживает основную проверку оборудования надолго
    private async Task RefreshModelActivityAsync(CancellationToken ct)
    {
        if (!IsEngineRunning || !IsCoordinatorRole || _engine.BaseUri is not { } address) return;
        var activity = await new EngineApiClient().ReadActivityAsync(address, ServerApiKey, ct);
        if (_disposed || !IsEngineRunning || _engine.BaseUri != address) return;
        _modelActivity = activity;
        OnPropertyChanged(nameof(ModelActivityText));
    }

    // Сдвиг KV-кэша определяется только по строке движка, без обещания семантического сжатия
    private void ObserveModelActivity(string line)
    {
        if (!IsEngineRunning || !line.Contains("context shift", StringComparison.OrdinalIgnoreCase)) return;
        _modelActivity = new(ModelActivityKind.ContextShift, 1);
        OnPropertyChanged(nameof(ModelActivityText));
    }
}
