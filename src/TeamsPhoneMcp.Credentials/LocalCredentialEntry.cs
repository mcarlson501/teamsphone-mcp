namespace TeamsPhoneMcp.Credentials;

/// <summary>
/// Configuration for a single named credential in the local (self-host) provider.
/// Bound from the <c>Credentials:&lt;credentialRef&gt;</c> configuration section.
/// The certificate is referenced either by thumbprint (looked up in an OS
/// certificate store) or by a PFX file path whose password is read from an
/// environment variable. No password or key material is ever stored in config.
/// </summary>
public sealed class LocalCredentialEntry
{
    /// <summary>Configuration root section that holds all named credentials.</summary>
    public const string SectionName = "Credentials";

    /// <summary>Entra directory (tenant) ID this credential authenticates against.</summary>
    public string? TenantId { get; set; }

    /// <summary>Entra application (client) ID used for app-only authentication.</summary>
    public string? ClientId { get; set; }

    /// <summary>Thumbprint of a certificate resident in an OS certificate store.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>
    /// Store location for thumbprint lookups: <c>CurrentUser</c> (default) or
    /// <c>LocalMachine</c>.
    /// </summary>
    public string CertificateStoreLocation { get; set; } = "CurrentUser";

    /// <summary>Path to a PFX file (alternative to <see cref="CertificateThumbprint"/>).</summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Name of the environment variable holding the PFX password. Used only with
    /// <see cref="CertificatePath"/>. The password value itself is never placed in
    /// configuration.
    /// </summary>
    public string? CertificatePasswordEnvVar { get; set; }
}
