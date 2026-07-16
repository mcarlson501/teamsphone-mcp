using System.Security.Cryptography.X509Certificates;

namespace TeamsPhoneMcp.Credentials;

/// <summary>
/// Resolved, ready-to-use credential material for authenticating to a single
/// Microsoft 365 tenant with an Entra application (app-only, certificate). Raw
/// certificate material never travels over MCP; it is produced here, server-side,
/// from a named <c>credentialRef</c> (build spec §6.5).
/// </summary>
public sealed class TenantCredential
{
    public TenantCredential(Guid tenantId, string clientId, X509Certificate2 certificate)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant ID must not be empty.", nameof(tenantId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(certificate);

        TenantId = tenantId;
        ClientId = clientId.Trim();
        Certificate = certificate;
    }

    /// <summary>The Entra directory (tenant) ID the credential authenticates against.</summary>
    public Guid TenantId { get; }

    /// <summary>The Entra application (client) ID used for app-only authentication.</summary>
    public string ClientId { get; }

    /// <summary>The authentication certificate (private key required for app-only sign-in).</summary>
    public X509Certificate2 Certificate { get; }
}
