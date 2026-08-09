namespace TeamsPhoneMcp.Host.Auth;

/// <summary>
/// Options controlling client-facing bearer-token authentication on the HTTP transport.
/// Tokens are supplied via configuration/environment only and are never hardcoded or logged.
/// </summary>
public sealed class BearerAuthOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Auth";

    /// <summary>Client id assigned to the single-token form, so it still attributes in audit.</summary>
    public const string DefaultClientId = "default";

    /// <summary>
    /// The single bearer token form, sourced from <c>TEAMSPHONE_MCP_BEARER_TOKEN</c> (or
    /// <c>Auth:BearerToken</c>). Equivalent to one <see cref="ClientTokens"/> entry named
    /// <see cref="DefaultClientId"/>; retained so existing installs keep working.
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// Named client tokens as <c>clientId -&gt; token</c>, from
    /// <c>Auth:ClientTokens:&lt;clientId&gt;</c>. Several are accepted at once so a token can be
    /// rotated with no downtime: add the new entry, move callers across, then remove the old one.
    /// The client id recorded in the audit trail is always the one matched here, never a value
    /// the client asserts.
    /// </summary>
    public IDictionary<string, string> ClientTokens { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Flattens both forms into the <c>clientId -&gt; token</c> set the middleware enforces.
    /// Values are trimmed to match how the <c>Bearer</c> header is parsed, and whitespace-only
    /// values are treated as unset — otherwise a stray space would leave the host looking
    /// configured while rejecting every request. When nothing is configured the result is empty
    /// and the server fails closed.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ResolveClientTokens()
    {
        var resolved = new List<KeyValuePair<string, string>>();

        if (!string.IsNullOrWhiteSpace(BearerToken))
        {
            resolved.Add(new KeyValuePair<string, string>(DefaultClientId, BearerToken.Trim()));
        }

        foreach (var (clientId, token) in ClientTokens)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                resolved.Add(new KeyValuePair<string, string>(clientId, token.Trim()));
            }
        }

        return resolved;
    }
}
