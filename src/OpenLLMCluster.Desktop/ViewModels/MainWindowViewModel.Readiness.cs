namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public string EngineReadinessTitle => Text[IsCoordinatorRole ? "ModelEngineReady" : "RpcEngineReady"];
    public string EngineReadinessDescription => Text[IsCoordinatorRole ? "ModelEngineReadyHelp" : "RpcEngineReadyHelp"];
    public string AgentReadinessDescription => Text[IsCoordinatorRole ? "MainAgentReadyHelp" : "WorkerAgentReadyHelp"];

    // Переводит пояснения и меняет назначение движка без создания отдельного состояния готовности
    private void RefreshReadinessLabels()
    {
        OnPropertyChanged(nameof(EngineReadinessTitle));
        OnPropertyChanged(nameof(EngineReadinessDescription));
        OnPropertyChanged(nameof(AgentReadinessDescription));
    }
}
