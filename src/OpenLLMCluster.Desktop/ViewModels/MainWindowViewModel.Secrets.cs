namespace OpenLLMCluster.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool _rememberAgentKeys = true;

    // Загружает только явно включённые секреты после успешного чтения профиля
    private void RestoreAgentKeys()
    {
        if (!RememberSettings || !RememberAgentKeys) return;
        try
        {
            var values = _store.Secrets.Load();
            LocalAgentToken = values.GetValueOrDefault("local", "");
            ServerApiKey = values.GetValueOrDefault("inference", "");
            foreach (var node in Nodes) node.AccessToken = values.GetValueOrDefault(SecretId(node), "");
        }
        catch (Exception ex) { Status = Text["SecretsReadFailed"] + " · " + ex.GetType().Name; Events.Add(Status); }
    }

    // Исключает ключи из profile.json; удалённые узлы не сохраняются в новом наборе
    private void SaveAgentKeys()
    {
        if (!RememberSettings) return;
        var values = new Dictionary<string, string>();
        if (RememberAgentKeys)
        {
            values["local"] = LocalAgentToken;
            if (!string.IsNullOrWhiteSpace(ServerApiKey)) values["inference"] = ServerApiKey;
            foreach (var node in Nodes) if (!string.IsNullOrWhiteSpace(node.AccessToken)) values[SecretId(node)] = node.AccessToken;
        }
        _store.Secrets.Save(values);
    }

    // Привязывает ключ к полному адресу управления и TLS-идентичности, а не отображаемому имени
    private static string SecretId(NodeViewModel node) => $"node:{node.UseTls}:{node.Host.Trim().ToLowerInvariant()}:{node.AgentPort}:{node.CertificateFingerprint}";
}
