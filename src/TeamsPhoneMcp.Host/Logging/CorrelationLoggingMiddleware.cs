using System.Text.RegularExpressions;

namespace TeamsPhoneMcp.Host.Logging;

/// <summary>
/// Lightweight structured-logging middleware that assigns/propagates a correlation id
/// for every HTTP request and pushes it onto the logging scope. This establishes the
/// correlation-id pattern early; the full audit pipeline (spec §9) arrives in M3.
/// </summary>
public sealed partial class CorrelationLoggingMiddleware
{
    /// <summary>Header used to read an inbound correlation id and echo it back.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    private const int MaxLoggedValueLength = 256;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationLoggingMiddleware> _logger;

    public CorrelationLoggingMiddleware(RequestDelegate next, ILogger<CorrelationLoggingMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Response.Headers[CorrelationHeader] = correlationId;

        // Sanitize user-controlled values before logging to prevent log forging.
        var method = Sanitize(context.Request.Method);
        var path = Sanitize(context.Request.Path.Value);

        using (_logger.BeginScope(new Dictionary<string, object> { ["correlationId"] = correlationId }))
        {
            _logger.LogInformation("Request {Method} {Path}", method, path);
            await _next(context);
        }
    }

    /// <summary>
    /// Accepts an inbound correlation id only when it is a short, safe token; otherwise
    /// generates a fresh GUID. This keeps attacker-controlled content out of logs.
    /// </summary>
    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(CorrelationHeader, out var value))
        {
            var candidate = value.ToString();
            if (SafeCorrelationId().IsMatch(candidate))
            {
                return candidate;
            }
        }

        return Guid.NewGuid().ToString("n");
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var trimmed = value.Length > MaxLoggedValueLength ? value[..MaxLoggedValueLength] : value;
        return ControlCharacters().Replace(trimmed, "_");
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SafeCorrelationId();

    [GeneratedRegex(@"\p{C}")]
    private static partial Regex ControlCharacters();
}
