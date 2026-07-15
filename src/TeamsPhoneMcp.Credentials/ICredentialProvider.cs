namespace TeamsPhoneMcp.Credentials;

/// <summary>
/// Resolves a named <c>credentialRef</c> to concrete tenant credential material.
/// Implementations map the name to a local secure store (self-host) or Azure Key
/// Vault (consultant mode). Raw secrets/certificates are produced only here and
/// are never accepted from, or returned to, the MCP client.
/// </summary>
public interface ICredentialProvider
{
    /// <summary>
    /// Resolves <paramref name="credentialRef"/> to tenant credential material.
    /// Throws <see cref="CredentialResolutionException"/> when the reference is
    /// unknown or its certificate cannot be loaded.
    /// </summary>
    ValueTask<TenantCredential> ResolveAsync(string credentialRef, CancellationToken cancellationToken);
}

/// <summary>
/// Raised when a credential reference cannot be resolved. The message is safe to
/// surface (it names the reference, never secret material).
/// </summary>
public sealed class CredentialResolutionException : Exception
{
    public CredentialResolutionException(string message)
        : base(message)
    {
    }

    public CredentialResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
