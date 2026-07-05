using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TeamsPhoneMcp.Host.Auth;

/// <summary>
/// Middleware that enforces a static bearer token on requests to the MCP endpoint.
/// Unauthenticated or wrong-token requests receive a 401 with no tool listing or
/// protocol payload leaked. The stdio transport is not routed through this middleware
/// and is treated as locally trusted (self-host, single-tenant model).
/// </summary>
public sealed class BearerAuthMiddleware
{
    // Reject tokens longer than this to prevent memory/CPU DoS via oversized headers.
    internal const int MaxTokenLength = 2048;

    private readonly RequestDelegate _next;
    private readonly ILogger<BearerAuthMiddleware> _logger;
    private readonly PathString _protectedPath;
    private readonly byte[]? _expectedTokenHash;

    public BearerAuthMiddleware(
        RequestDelegate next,
        ILogger<BearerAuthMiddleware> logger,
        IOptions<BearerAuthOptions> options,
        string protectedPath = "/mcp")
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _protectedPath = protectedPath;

        var token = options.Value.BearerToken;
        // Store the SHA-256 hash of the expected token so the comparison is
        // always between two fixed-length (32-byte) values, preventing
        // length-based timing leaks from CryptographicOperations.FixedTimeEquals.
        _expectedTokenHash = string.IsNullOrEmpty(token)
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(_protectedPath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (_expectedTokenHash is null)
        {
            // Fail closed: no token configured means nothing can authenticate.
            _logger.LogWarning(
                "Rejected request to {Path}: no bearer token is configured (set TEAMSPHONE_MCP_BEARER_TOKEN).",
                _protectedPath);
            await RejectAsync(context);
            return;
        }

        if (!TryGetBearerToken(context, out var presented) || !IsValid(presented))
        {
            _logger.LogWarning("Rejected unauthenticated request to {Path}.", _protectedPath);
            await RejectAsync(context);
            return;
        }

        await _next(context);
    }

    private static bool TryGetBearerToken(HttpContext context, out byte[] token)
    {
        token = Array.Empty<byte>();
        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = header[scheme.Length..].Trim();
        // Reject oversized tokens before allocating to prevent memory/CPU DoS.
        if (value.Length == 0 || value.Length > MaxTokenLength)
        {
            return false;
        }

        token = Encoding.UTF8.GetBytes(value);
        return true;
    }

    // Hash the presented token to the same fixed length as the stored hash so
    // CryptographicOperations.FixedTimeEquals always receives equal-length spans,
    // eliminating the early-exit length leak.
    private bool IsValid(byte[] presented) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(presented), _expectedTokenHash!);

    private static async Task RejectAsync(HttpContext context)
    {
        // 401 with an empty body: never leak the tool listing or protocol details.
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.CompleteAsync();
    }
}
