using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TeamsPhoneMcp.Audit;
using TeamsPhoneMcp.Core;
using TeamsPhoneMcp.Host.Auth;
using TeamsPhoneMcp.Host.Logging;
using TeamsPhoneMcp.Host.RateLimiting;

namespace TeamsPhoneMcp.Host;

/// <summary>
/// Entry point for the teamsphone-mcp host.
/// Supports two transports:
///   • Streamable HTTP at <c>/mcp</c> (primary; bearer-token protected)
///   • stdio for local single-tenant use (selected with <c>--stdio</c> or
///     <c>TEAMSPHONE_MCP_STDIO=true</c>; treated as locally trusted, no bearer)
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        if (UseStdio(args))
        {
            await RunStdioAsync(args);
        }
        else
        {
            await RunHttpAsync(args);
        }
    }

    internal static bool UseStdio(string[] args) =>
        args.Contains("--stdio", StringComparer.OrdinalIgnoreCase) ||
        string.Equals(
            Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_STDIO"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static async Task RunStdioAsync(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        // stdout is reserved for the MCP protocol on stdio; route logs to stderr.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .AddTeamsPhoneTools()
            .AddPowerShellStageExecution()
            .AddLocalTenantCredentials();
        builder.Services.AddTeamsPhoneAudit(builder.Environment);

        await builder.Build().RunAsync();
    }

    private static async Task RunHttpAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // The credential is sourced from config/env only. TEAMSPHONE_MCP_BEARER_TOKEN
        // maps to Auth:BearerToken so operators can use a single, well-known env var.
        builder.Services
            .AddOptions<BearerAuthOptions>()
            .Bind(builder.Configuration.GetSection(BearerAuthOptions.SectionName))
            .PostConfigure(options =>
            {
                var envToken = builder.Configuration["TEAMSPHONE_MCP_BEARER_TOKEN"];
                if (!string.IsNullOrEmpty(envToken))
                {
                    options.BearerToken = envToken;
                }
            })
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<BearerAuthOptions>, BearerAuthOptionsValidator>();
        builder.Services
            .AddOptions<McpRateLimitOptions>()
            .Bind(builder.Configuration.GetSection(McpRateLimitOptions.SectionName))
            .Validate(
                options => options.PermitLimit > 0,
                $"{McpRateLimitOptions.SectionName}:PermitLimit must be greater than zero.")
            .Validate(
                options => options.Window > TimeSpan.Zero,
                $"{McpRateLimitOptions.SectionName}:Window must be greater than zero.")
            .ValidateOnStart();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = static (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds))
                            .ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
            options.AddPolicy(McpRateLimitPolicy.Name, context =>
            {
                var settings = context.RequestServices
                    .GetRequiredService<IOptions<McpRateLimitOptions>>()
                    .Value;
                return RateLimitPartition.GetFixedWindowLimiter(
                    McpRateLimitPolicy.GetPartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = settings.PermitLimit,
                        QueueLimit = 0,
                        Window = settings.Window
                    });
            });
        });

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
                // Keep Streamable HTTP sessions alive. As of the 2026-07-28 protocol revision
                // (SEP-2567) the SDK defaults Stateless to true, which drops Mcp-Session-Id
                // entirely. Session ids are load-bearing here: confirmation tokens are bound to
                // them (Policy/ConfirmationTokenService), session ownership is claimed per client
                // (Policy/McpSessionOwnershipStore), and rate limiting partitions on them
                // (RateLimiting/McpRateLimitPolicy). Revisit only alongside a replacement binding.
                options.Stateless = false;
            })
            .AddTeamsPhoneTools()
            .AddPowerShellStageExecution()
            .AddLocalTenantCredentials();
        builder.Services.AddTeamsPhoneAudit(builder.Environment);

        var app = builder.Build();

        WarnIfNoToken(app);

        app.UseMiddleware<CorrelationLoggingMiddleware>();
        app.UseMiddleware<BearerAuthMiddleware>();
        app.UseRateLimiter();

        app.MapMcp("/mcp").RequireRateLimiting(McpRateLimitPolicy.Name);

        await app.RunAsync();
    }

    private static void WarnIfNoToken(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<BearerAuthOptions>>().Value;
        if (string.IsNullOrEmpty(options.BearerToken))
        {
            app.Logger.LogWarning(
                "No bearer token configured; all requests to /mcp will be rejected. " +
                "Set TEAMSPHONE_MCP_BEARER_TOKEN to enable the HTTP transport.");
        }
    }
}
