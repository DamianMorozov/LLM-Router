namespace OpenLLMCluster.Infrastructure;

/// <summary>Постоянная TLS-идентичность узла с приватным ключом в отдельном защищённом хранилище</summary>
public sealed class AgentCertificateStore(string? path = null)
{
    private readonly ProtectedSecretStore _store = new(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Open-LLM-Cluster", "agent-identity.dat"));
    public string FilePath => _store.FilePath;

    /// <summary>Создаёт сертификат один раз; отпечаток сверяется пользователем через отдельный канал</summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2 GetOrCreate()
    {
        var values = _store.Load();
        if (!values.TryGetValue("certificate", out var encoded))
        {
            using var rsa = RSA.Create(3072);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Open-LLM-Cluster Agent", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, true));
            var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddDnsName(Environment.MachineName);
            names.AddIpAddress(IPAddress.Loopback);
            names.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
            encoded = Convert.ToBase64String(created.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
            _store.Save(new() { ["certificate"] = encoded });
        }
        // SChannel требует UserKeySet; macOS использует временный keychain без неподдерживаемого EphemeralKeySet
        // PersistKeySet не задаётся: постоянная идентичность остаётся только в защищённом хранилище
        var flags = OperatingSystem.IsWindows() ? System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.UserKeySet
            : OperatingSystem.IsMacOS() ? System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.DefaultKeySet
                : System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet;
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(encoded), null, flags);
    }

    /// <summary>Возвращает полный SHA-256 сертификата без приватного ключа</summary>
    public string Fingerprint()
    {
        using var certificate = GetOrCreate();
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>Сравнивает закреплённую TLS-идентичность, включая срок действия, без отключения проверки для других узлов</summary>
    public static bool Matches(System.Security.Cryptography.X509Certificates.X509Certificate2? certificate, string fingerprint)
    {
        if (certificate is null || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow) return false;
        var normalized = fingerprint.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return normalized.Length == 64 && normalized.All(char.IsAsciiHexDigit)
            && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(normalized));
    }
}
