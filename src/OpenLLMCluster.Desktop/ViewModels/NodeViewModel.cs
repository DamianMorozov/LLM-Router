namespace OpenLLMCluster.Desktop.ViewModels;

/// <summary>Редактируемый узел; ключ передаётся отдельно в защищённое хранилище, а не в профиль</summary>
public partial class NodeViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int _agentPort = 5100;
    [ObservableProperty] private int _rpcPort = 50052;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _accessToken = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _useTls;
    [ObservableProperty] private string _certificateFingerprint = "";
    [ObservableProperty] private bool _isRemoteBusy;
    [ObservableProperty] private string _remoteLog = "";
    [ObservableProperty] private string _remoteRpcAddress = "0.0.0.0";
    public Localizer Text { get; }

    /// <summary>Создаёт редактор узла из сохранённого профиля</summary>
    public NodeViewModel(NodeEndpoint node, Localizer text)
    {
        Text = text;
        Name = node.Name;
        Host = node.Host;
        AgentPort = node.AgentPort;
        RpcPort = node.RpcPort;
        Enabled = node.Enabled;
        UseTls = node.UseTls;
        CertificateFingerprint = node.CertificateFingerprint;
        RemoteRpcAddress = node.RpcListenAddress;
    }

    /// <summary>Возвращает сохраняемую часть настроек</summary>
    public NodeEndpoint ToEndpoint() => new() { Name = Name.Trim(), Host = Host.Trim(), AgentPort = AgentPort, RpcPort = RpcPort, Enabled = Enabled,
        UseTls = UseTls, CertificateFingerprint = CertificateFingerprint.Trim(), RpcListenAddress = RemoteRpcAddress.Trim() };
}
