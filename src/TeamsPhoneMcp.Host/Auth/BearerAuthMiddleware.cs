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
    private readonly RequestDelegate _next;
    private readonly ILogger<BearerAuthMiddleware> _logger;
    private readonly PathString _protectedPath;
    private readonly byte[]? _expectedTokenBytes;

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
        _expectedTokenBytes = string.IsNullOrEmpty(token) ? null : Encoding.UTF8.GetBytes(token);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(_protectedPath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (_expectedTokenBytes is null)
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
        if (value.Length == 0)
        {
            return false;
        }

        token = Encoding.UTF8.GetBytes(value);
        return true;
    }

    private bool IsValid(byte[] presented) =>
        CryptographicOperations.FixedTimeEquals(presented, _expectedTokenBytes!);

    private static async Task RejectAsync(HttpContext context)
    {
        // 401 with an empty body: never leak the tool listing or protocol details.
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.CompleteAsync();
    }
}
