using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TeamsPhoneMcp.Credentials;

/// <summary>
/// Self-host credential provider (build spec §4, credential provider v1). Maps a
/// named <c>credentialRef</c> to an Entra app-only certificate resolved from
/// configuration plus an OS certificate store or a PFX file. Certificate material
/// is loaded lazily on resolve and never logged.
/// </summary>
public sealed class LocalCredentialProvider : ICredentialProvider
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalCredentialProvider> _logger;
    private readonly Func<LocalCredentialEntry, X509Certificate2> _certificateLoader;

    public LocalCredentialProvider(IConfiguration configuration, ILogger<LocalCredentialProvider> logger)
        : this(configuration, logger, LoadCertificate)
    {
    }

    /// <summary>Test seam: allows injecting a certificate loader in place of OS/file access.</summary>
    public LocalCredentialProvider(
        IConfiguration configuration,
        ILogger<LocalCredentialProvider> logger,
        Func<LocalCredentialEntry, X509Certificate2> certificateLoader)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(certificateLoader);

        _configuration = configuration;
        _logger = logger;
        _certificateLoader = certificateLoader;
    }

    public ValueTask<TenantCredential> ResolveAsync(string credentialRef, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(credentialRef))
        {
            throw new CredentialResolutionException("A credentialRef is required.");
        }

        var entry = ReadEntry(credentialRef.Trim());
        if (!Guid.TryParse(entry.TenantId, out var tenantId) || tenantId == Guid.Empty)
        {
            throw new CredentialResolutionException(
                $"Credential '{credentialRef}' has a missing or invalid TenantId.");
        }

        if (string.IsNullOrWhiteSpace(entry.ClientId))
        {
            throw new CredentialResolutionException(
                $"Credential '{credentialRef}' has a missing ClientId.");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = _certificateLoader(entry);
        }
        catch (CredentialResolutionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never surface raw store/file errors (may contain paths); log and sanitize.
            _logger.LogError(ex, "Failed to load the certificate for credential '{CredentialRef}'.", credentialRef);
            throw new CredentialResolutionException(
                $"The certificate for credential '{credentialRef}' could not be loaded.");
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new CredentialResolutionException(
                $"The certificate for credential '{credentialRef}' has no private key; app-only sign-in requires one.");
        }

        _logger.LogInformation(
            "Resolved credential '{CredentialRef}' for tenant {TenantId} using certificate thumbprint ending {ThumbprintSuffix}.",
            credentialRef,
            tenantId,
            SafeThumbprintSuffix(certificate.Thumbprint));

        return ValueTask.FromResult(new TenantCredential(tenantId, entry.ClientId!, certificate));
    }

    private LocalCredentialEntry ReadEntry(string credentialRef)
    {
        var section = _configuration.GetSection($"{LocalCredentialEntry.SectionName}:{credentialRef}");
        if (!section.Exists())
        {
            throw new CredentialResolutionException($"No credential named '{credentialRef}' is configured.");
        }

        var entry = new LocalCredentialEntry
        {
            TenantId = section["TenantId"],
            ClientId = section["ClientId"],
            CertificateThumbprint = section["CertificateThumbprint"],
            CertificatePath = section["CertificatePath"],
            CertificatePasswordEnvVar = section["CertificatePasswordEnvVar"],
        };

        var storeLocation = section["CertificateStoreLocation"];
        if (!string.IsNullOrWhiteSpace(storeLocation))
        {
            entry.CertificateStoreLocation = storeLocation;
        }

        return entry;
    }

    private static X509Certificate2 LoadCertificate(LocalCredentialEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.CertificatePath))
        {
            return LoadFromFile(entry);
        }

        if (!string.IsNullOrWhiteSpace(entry.CertificateThumbprint))
        {
            return LoadFromStore(entry);
        }

        throw new CredentialResolutionException(
            "A credential must set either CertificateThumbprint or CertificatePath.");
    }

    private static X509Certificate2 LoadFromFile(LocalCredentialEntry entry)
    {
        string? password = null;
        if (!string.IsNullOrWhiteSpace(entry.CertificatePasswordEnvVar))
        {
            password = Environment.GetEnvironmentVariable(entry.CertificatePasswordEnvVar);
        }

        // EphemeralKeySet keeps the key in-process (no user-store writes) on Windows
        // and Linux, but it is not supported on macOS and throws there; fall back to
        // the default key set on macOS.
        var flags = OperatingSystem.IsMacOS()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

        return new X509Certificate2(
            entry.CertificatePath!,
            password,
            flags);
    }

    private static X509Certificate2 LoadFromStore(LocalCredentialEntry entry)
    {
        if (!Enum.TryParse<StoreLocation>(entry.CertificateStoreLocation, ignoreCase: true, out var location))
        {
            throw new CredentialResolutionException(
                $"Invalid CertificateStoreLocation '{entry.CertificateStoreLocation}'. Use CurrentUser or LocalMachine.");
        }

        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        var normalizedThumbprint = entry.CertificateThumbprint!
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalizedThumbprint, validOnly: false);
        if (matches.Count == 0)
        {
            throw new CredentialResolutionException(
                $"No certificate with the configured thumbprint was found in {location}\\My.");
        }

        return matches[0];
    }

    private static string SafeThumbprintSuffix(string? thumbprint) =>
        string.IsNullOrEmpty(thumbprint) || thumbprint.Length < 4
            ? "****"
            : thumbprint[^4..];
}
