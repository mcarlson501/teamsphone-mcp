namespace TeamsPhoneMcp.Host.Auth;

/// <summary>
/// Options controlling client-facing bearer-token authentication on the HTTP transport.
/// The token is supplied via configuration/environment only and is never hardcoded or logged.
/// </summary>
public sealed class BearerAuthOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Auth";

    /// <summary>
    /// The static bearer token required on requests to the MCP endpoint.
    /// Sourced from <c>TEAMSPHONE_MCP_BEARER_TOKEN</c> (or <c>Auth:BearerToken</c>).
    /// When null/empty the server fails closed: every protected request is rejected.
    /// </summary>
    public string? BearerToken { get; set; }
}
