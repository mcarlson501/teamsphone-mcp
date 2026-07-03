namespace TeamsPhoneMcp.Host.Logging;

/// <summary>
/// Lightweight structured-logging middleware that assigns/propagates a correlation id
/// for every HTTP request and pushes it onto the logging scope. This establishes the
/// correlation-id pattern early; the full audit pipeline (spec §9) arrives in M3.
/// </summary>
public sealed class CorrelationLoggingMiddleware
{
    /// <summary>Header used to read an inbound correlation id and echo it back.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationLoggingMiddleware> _logger;

    public CorrelationLoggingMiddleware(RequestDelegate next, ILogger<CorrelationLoggingMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(CorrelationHeader, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value.ToString()
                : Guid.NewGuid().ToString("n");

        context.Response.Headers[CorrelationHeader] = correlationId;

        using (_logger.BeginScope(new Dictionary<string, object> { ["correlationId"] = correlationId }))
        {
            _logger.LogInformation(
                "Request {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
            await _next(context);
        }
    }
}
