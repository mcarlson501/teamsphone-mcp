namespace TeamsPhoneMcp.Host.RateLimiting;

internal static class McpRateLimitPolicy
{
    public const string Name = "mcp-session";

    internal const int MaxSessionIdLength = McpSessionHeader.MaxLength;

    public static string GetPartitionKey(HttpContext context)
    {
        // Anything the shared reader will not vouch for falls back to a per-caller partition,
        // so an ambiguous session header cannot buy a fresh rate-limit bucket.
        return McpSessionHeader.Read(context, out var sessionId) == McpSessionHeaderState.Valid
            ? $"session:{sessionId}"
            : $"client:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
