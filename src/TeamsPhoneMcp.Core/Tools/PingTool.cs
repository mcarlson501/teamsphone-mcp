using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TeamsPhoneMcp.Core.Tools;

/// <summary>
/// A trivial, read-only diagnostic tool used to prove transport, registration,
/// and auth end-to-end. It performs no tenant connection and runs no PowerShell.
/// </summary>
[McpServerToolType]
public class PingTool
{
    [McpServerTool(Name = "ping", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Health-check tool that echoes back an optional message. Proves the server is reachable and authenticated.")]
    public static PingResult Ping(
        [Description("Optional message to echo back.")] string? message = null)
    {
        return new PingResult(
            Ok: true,
            Message: string.IsNullOrWhiteSpace(message) ? "pong" : message,
            ServerTimeUtc: DateTimeOffset.UtcNow);
    }
}

/// <summary>Structured result returned by the <c>ping</c> tool.</summary>
public sealed record PingResult(bool Ok, string Message, DateTimeOffset ServerTimeUtc);
