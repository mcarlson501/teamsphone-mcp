using System.Text;
using Microsoft.Extensions.Options;
using TeamsPhoneMcp.Core.Policy;

namespace TeamsPhoneMcp.Host.Auth;

/// <summary>
/// Middleware that enforces the configured bearer tokens on requests to the MCP endpoint.
/// Unauthenticated or wrong-token requests receive a 401 with no tool listing or
/// protocol payload leaked. The stdio transport is not routed through this middleware
/// and is treated as locally trusted (self-host, single-tenant model).
/// <para>
/// On success the matched client id is bound to the MCP session, so audit attribution and
/// confirmation-token binding both use a server-derived identity rather than one the client
/// asserts (build spec §15 S2).
/// </para>
/// </summary>
public sealed class BearerAuthMiddleware
{
    // Reject tokens longer than this to prevent memory/CPU DoS via oversized headers.
    internal const int MaxTokenLength = 2048;

    private readonly RequestDelegate _next;
    private readonly ILogger<BearerAuthMiddleware> _logger;
    private readonly PathString _protectedPath;
    private readonly ClientTokenRegistry _clients;
    private readonly McpSessionOwnershipStore _sessionOwnership;
    private readonly AuthenticatedClientAccessor _clientAccessor;

    public BearerAuthMiddleware(
        RequestDelegate next,
        ILogger<BearerAuthMiddleware> logger,
        IOptions<BearerAuthOptions> options,
        McpSessionOwnershipStore sessionOwnership,
        AuthenticatedClientAccessor clientAccessor,
        string protectedPath = "/mcp")
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionOwnership = sessionOwnership ?? throw new ArgumentNullException(nameof(sessionOwnership));
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _protectedPath = protectedPath;

        ArgumentNullException.ThrowIfNull(options);
        foreach (var (clientId, token) in options.Value.ResolveClientTokens())
        {
            if (token.Length > MaxTokenLength)
            {
                throw new ArgumentException(
                    $"Configured bearer token for client '{clientId}' exceeds the maximum allowed " +
                    $"length of {MaxTokenLength} characters.",
                    nameof(options));
            }
        }

        _clients = new ClientTokenRegistry(options.Value);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(_protectedPath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (_clients.IsEmpty)
        {
            // Fail closed: no token configured means nothing can authenticate.
            _logger.LogWarning(
                "Rejected request to {Path}: no bearer token is configured (set " +
                "TEAMSPHONE_MCP_BEARER_TOKEN or Auth:ClientTokens).",
                _protectedPath);
            await RejectAsync(context);
            return;
        }

        ClientAuthentication authentication = default;
        if (TryGetBearerToken(context, out var presented))
        {
            authentication = _clients.Authenticate(presented);
        }

        if (!authentication.IsAuthenticated)
        {
            _logger.LogWarning("Rejected unauthenticated request to {Path}.", _protectedPath);
            await RejectAsync(context);
            return;
        }

        var clientId = authentication.ClientId!;
        if (!TryClaimSession(context, clientId))
        {
            _logger.LogWarning(
                "Rejected request to {Path} from client {ClientId}: the supplied MCP session belongs " +
                "to a different client.",
                _protectedPath,
                clientId);
            await RejectAsync(context);
            return;
        }

        // The MCP message for this request is dispatched inside _next, so tools resolve the
        // caller from this scope rather than from anything the client sent.
        using var clientScope = _clientAccessor.Enter(clientId, authentication.WhatIfMode);
        await _next(context);
    }

    /// <summary>
    /// Claims the MCP session for the authenticated client. The session header is absent on
    /// <c>initialize</c> (the id is issued in that response), so an unclaimed request is allowed
    /// through and claimed on the next call that carries the header. An ambiguous header is
    /// refused rather than ignored: reading it loosely would claim a session id that no other
    /// component resolves, leaving the real one unowned.
    /// </summary>
    private bool TryClaimSession(HttpContext context, string clientId) =>
        McpSessionHeader.Read(context, out var sessionId) switch
        {
            McpSessionHeaderState.Absent => true,
            McpSessionHeaderState.Valid => _sessionOwnership.TryClaim(sessionId, clientId),
            _ => false,
        };

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

    private static async Task RejectAsync(HttpContext context)
    {
        // 401 with an empty body: never leak the tool listing or protocol details.
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.CompleteAsync();
    }
}
