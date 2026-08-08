using Microsoft.Extensions.Primitives;

namespace TeamsPhoneMcp.Host.RateLimiting;

internal static class McpRateLimitPolicy
{
    public const string Name = "mcp-session";
    internal const int MaxSessionIdLength = 128;
    private const string SessionHeaderName = "Mcp-Session-Id";

    public static string GetPartitionKey(HttpContext context)
    {
        StringValues sessionHeader = context.Request.Headers[SessionHeaderName];
        if (sessionHeader.Count == 1 && IsValidSessionId(sessionHeader[0]))
        {
            return $"session:{sessionHeader[0]}";
        }

        return $"client:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

    private static bool IsValidSessionId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length > MaxSessionIdLength)
        {
            return false;
        }

        foreach (var character in sessionId)
        {
            if (!IsHttpTokenCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHttpTokenCharacter(char character) =>
        character is >= '0' and <= '9' or
            >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or
            '^' or '_' or '`' or '|' or '~';
}