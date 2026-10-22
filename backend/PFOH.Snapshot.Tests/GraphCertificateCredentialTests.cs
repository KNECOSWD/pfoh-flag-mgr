using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Xunit;

namespace PFOH.Snapshot.Tests;

public class GraphCertificateCredentialTests
{
    [Fact]
    public async Task Certificate_credential_is_cached_for_one_run_and_reloaded_after_it()
    {
        var secret = Convert.ToBase64String(CreatePfx());
        var secrets = new FakeSecrets(secret);
        var source = new GraphCertificateCredentialSource(secrets, "pfoh-snapshot-graph", "77f89427-b4a7-43ed-92fc-3eb5bd63d0f1", "graph-app");

        var first = await source.GetAsync(CancellationToken.None);
        var second = await source.GetAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.IsType<ClientCertificateCredential>(first);
        Assert.Equal(1, secrets.Calls);
        Assert.Equal("77f89427-b4a7-43ed-92fc-3eb5bd63d0f1", source.TenantId);
        Assert.Equal("graph-app", source.ClientId);

        source.ReleaseAfterRun();
        var third = await source.GetAsync(CancellationToken.None);
        Assert.IsType<ClientCertificateCredential>(third);
        Assert.NotSame(first, third);
        Assert.Equal(2, secrets.Calls);
    }

    [Fact]
    public void Rejected_secret_text_does_not_include_the_value()
    {
        var secret = "not-a-certificate";
        var error = Assert.Throws<InvalidOperationException>(() => GraphCertificateMaterial.FromSecretValue(secret));
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Directory_read_uses_one_graph_token_for_both_selects()
    {
        var credential = new FakeGraphCredential();
        var source = new FakeCredentialSource(credential);
        var handler = new RecordingHandler();
        var directory = new GraphDirectorySource(source, new HttpClient(handler));

        var users = await directory.ReadAsync(CancellationToken.None);

        Assert.Equal(1, source.Gets);
        Assert.Equal(1, credential.Calls);
        Assert.Equal("https://graph.microsoft.com/.default", Assert.Single(credential.Scopes!));
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains(SnapshotFieldSpec.DirectoryUserSelect, Uri.UnescapeDataString(handler.Urls[0]), StringComparison.Ordinal);
        Assert.Contains(SnapshotFieldSpec.SignInActivitySelect, Uri.UnescapeDataString(handler.Urls[1]), StringComparison.Ordinal);
        Assert.All(handler.Authorizations, value => Assert.Equal("Bearer", value));
        Assert.Equal("Ada", users[0].DisplayName);
        Assert.Equal("2026-01-01T00:00:00Z", users[0].SignInActivity!.LastSignInDateTime);
        Assert.Equal(1, source.Releases);
    }

    private static byte[] CreatePfx()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=pfoh-snapshot-graph-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(12));
        return certificate.Export(X509ContentType.Pfx);
    }

    private sealed class FakeSecrets(string value) : IKeyVaultCertificateSecrets
    {
        public int Calls { get; private set; }

        public Task<string> GetSecretValueAsync(string name, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("pfoh-snapshot-graph", name);
            return Task.FromResult(value);
        }
    }

    private sealed class FakeCredentialSource(TokenCredential credential) : IGraphCredentialSource
    {
        public int Gets { get; private set; }

        public int Releases { get; private set; }

        public Task<TokenCredential> GetAsync(CancellationToken ct)
        {
            Gets++;
            return Task.FromResult(credential);
        }

        public void ReleaseAfterRun()
        {
            Releases++;
        }
    }

    private sealed class FakeGraphCredential : TokenCredential
    {
        public int Calls { get; private set; }

        public string[]? Scopes { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return GetTokenAsync(requestContext, cancellationToken).GetAwaiter().GetResult();
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            Scopes = requestContext.Scopes.ToArray();
            return ValueTask.FromResult(new AccessToken("unit-test-token", DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        public List<string> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            Authorizations.Add(request.Headers.Authorization!.Scheme);
            var body = request.RequestUri.Query.Contains("signInActivity", StringComparison.Ordinal)
                ? """{"value":[{"id":"1","signInActivity":{"lastSignInDateTime":"2026-01-01T00:00:00Z","lastSuccessfulSignInDateTime":"2026-01-01T00:00:00Z"}}]}"""
                : """{"value":[{"id":"1","displayName":"Ada","mail":"ada@example.com","accountEnabled":true}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
