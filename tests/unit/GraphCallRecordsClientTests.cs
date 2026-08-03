using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TeamsPhoneMcp.Core.Tools;
using TeamsPhoneMcp.Credentials;

namespace TeamsPhoneMcp.UnitTests;

public sealed class GraphCallRecordsClientTests
{
    [Fact]
    public async Task TenantMismatch_FailsBeforeGraphAccess()
    {
        var credentialTenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var requestedTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var client = new GraphCallRecordsClient(new FakeCredentialProvider(CreateCredential(credentialTenant)));

        var exception = await Assert.ThrowsAsync<GraphCallRecordsException>(() =>
            client.GetPstnCallsAsync(
                new GraphTenantContext(requestedTenant, "tenant-a"),
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
                CancellationToken.None));

        Assert.Equal("authenticationFailed", exception.ErrorCode);
        Assert.DoesNotContain("tenant-a", exception.Message);
    }

    [Fact]
    public async Task CredentialResolutionFailure_DoesNotExposeProviderDetails()
    {
        var provider = new FailingCredentialProvider();
        var client = new GraphCallRecordsClient(provider);

        var exception = await Assert.ThrowsAsync<GraphCallRecordsException>(() =>
            client.GetPstnCallsAsync(
                new GraphTenantContext(Guid.NewGuid(), "secret-credential-name"),
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
                CancellationToken.None));

        Assert.Equal("authenticationFailed", exception.ErrorCode);
        Assert.Equal("The tenant credential could not be resolved.", exception.Message);
        Assert.DoesNotContain("secret-credential-name", exception.Message);
    }

    private static TenantCredential CreateCredential(Guid tenantId)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=graph-client-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new TenantCredential(tenantId, Guid.NewGuid().ToString(), certificate);
    }

    private sealed class FakeCredentialProvider(TenantCredential credential) : ICredentialProvider
    {
        public ValueTask<TenantCredential> ResolveAsync(string credentialRef, CancellationToken cancellationToken) =>
            ValueTask.FromResult(credential);
    }

    private sealed class FailingCredentialProvider : ICredentialProvider
    {
        public ValueTask<TenantCredential> ResolveAsync(string credentialRef, CancellationToken cancellationToken) =>
            throw new CredentialResolutionException($"Credential '{credentialRef}' was not found at /private/path.");
    }
}