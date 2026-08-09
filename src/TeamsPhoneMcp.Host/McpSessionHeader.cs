namespace TeamsPhoneMcp.Host;

/// <summary>How the <c>Mcp-Session-Id</c> header on a request should be treated.</summary>
internal enum McpSessionHeaderState
{
    /// <summary>No session header; the request is not yet bound to a session.</summary>
    Absent,

    /// <summary>Exactly one well-formed session id.</summary>
    Valid,

    /// <summary>Repeated, empty, over-long, or otherwise unusable.</summary>
    Malformed,
}

/// <summary>
/// The single reader for the <c>Mcp-Session-Id</c> request header, shared by rate limiting and
/// auth so the two cannot disagree about which session a request belongs to. A repeated header
/// is reported as <see cref="McpSessionHeaderState.Malformed"/> rather than absent, because
/// <c>StringValues.ToString()</c> would silently join the values into one that matches neither.
/// </summary>
internal static class McpSessionHeader
{
    public const string Name = "Mcp-Session-Id";

    internal const int MaxLength = 128;

    public static McpSessionHeaderState Read(HttpContext context, out string sessionId)
    {
        ArgumentNullException.ThrowIfNull(context);

        var header = context.Request.Headers[Name];
        sessionId = string.Empty;

        if (header.Count == 0)
        {
            return McpSessionHeaderState.Absent;
        }

        if (header.Count > 1 || !IsWellFormed(header[0]))
        {
            return McpSessionHeaderState.Malformed;
        }

        sessionId = header[0]!;
        return McpSessionHeaderState.Valid;
    }

    private static bool IsWellFormed(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId) &&
        sessionId.Length <= MaxLength &&
        sessionId.All(IsHttpTokenCharacter);

    private static bool IsHttpTokenCharacter(char character) =>
        character is >= '0' and <= '9' or
            >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or
            '^' or '_' or '`' or '|' or '~';
}
