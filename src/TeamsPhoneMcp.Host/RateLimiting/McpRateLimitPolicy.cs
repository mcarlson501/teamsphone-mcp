using Microsoft.Extensions.Primitives;

namespace TeamsPhoneMcp.Host.RateLimiting;

internal static class McpRateLimitPolicy
{
    public const string Name = "mcp-session";
    private const string SessionHeaderName = "Mcp-Session-Id";

    public static string GetPartitionKey(HttpContext context)
    {
        StringValues sessionHeader = context.Request.Headers[SessionHeaderName];
        var sessionId = sessionHeader.ToString().Trim();
        if (sessionId.Length > 0)
        {
            return $"session:{sessionId}";
        }

        return $"client:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}