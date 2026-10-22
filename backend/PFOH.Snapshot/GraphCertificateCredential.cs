using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace PFOH.Snapshot;

/// <summary>
/// Loads the Graph app certificate from Key Vault. The private key is not written to a file and is not logged.
/// </summary>
public interface IKeyVaultCertificateSecrets
{
    Task<string> GetSecretValueAsync(string name, CancellationToken ct);
}

public sealed class SecretClientCertificateSecrets(SecretClient client) : IKeyVaultCertificateSecrets
{
    public async Task<string> GetSecretValueAsync(string name, CancellationToken ct)
    {
        var response = await client.GetSecretAsync(name, cancellationToken: ct);
        return response.Value.Value;
    }
}

public interface IGraphCredentialSource
{
    Task<TokenCredential> GetAsync(CancellationToken ct);

    void ReleaseAfterRun();
}

/// <summary>
/// One certificate credential for a snapshot run. The next run loads Key Vault again.
/// </summary>
public sealed class GraphCertificateCredentialSource : IGraphCredentialSource
{
    private readonly IKeyVaultCertificateSecrets secrets;
    private readonly string certificateName;
    private readonly string tenantId;
    private readonly string clientId;
    private ClientCertificateCredential? cached;

    public GraphCertificateCredentialSource(
        IKeyVaultCertificateSecrets secrets,
        string certificateName,
        string tenantId,
        string clientId)
    {
        this.secrets = secrets;
        this.certificateName = certificateName;
        this.tenantId = tenantId;
        this.clientId = clientId;
    }

    public string TenantId => tenantId;

    public string ClientId => clientId;

    public string CertificateName => certificateName;

    public async Task<TokenCredential> GetAsync(CancellationToken ct)
    {
        if (cached is not null)
        {
            return cached;
        }

        var secretValue = await secrets.GetSecretValueAsync(certificateName, ct);
        var certificate = GraphCertificateMaterial.FromSecretValue(secretValue);
        cached = new ClientCertificateCredential(tenantId, clientId, certificate);
        return cached;
    }

    public void ReleaseAfterRun()
    {
        cached = null;
    }

    public static IGraphCredentialSource Create(
        Uri vaultUri,
        TokenCredential credential,
        string certificateName,
        string tenantId,
        string clientId)
    {
        var secrets = new SecretClient(vaultUri, credential);
        return new GraphCertificateCredentialSource(
            new SecretClientCertificateSecrets(secrets),
            certificateName,
            tenantId,
            clientId);
    }
}

public static class GraphCertificateMaterial
{
    public static X509Certificate2 FromSecretValue(string secretValue)
    {
        if (string.IsNullOrWhiteSpace(secretValue))
        {
            throw new InvalidOperationException("The Key Vault certificate secret was empty.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(secretValue);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The Key Vault certificate secret was not a certificate.");
        }

        try
        {
            return new X509Certificate2(bytes, string.Empty, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The Key Vault certificate secret was not a certificate.");
        }
    }
}
