using Microsoft.Extensions.Options;
using TeamsPhoneMcp.Core;
using TeamsPhoneMcp.Host.Auth;
using TeamsPhoneMcp.Host.Logging;

namespace TeamsPhoneMcp.Host;

/// <summary>
/// Entry point for the teamsphone-mcp host (M0 skeleton).
/// Supports two transports:
///   • Streamable HTTP at <c>/mcp</c> (primary; bearer-token protected)
///   • stdio for local single-tenant use (selected with <c>--stdio</c> or
///     <c>TEAMSPHONE_MCP_STDIO=true</c>; treated as locally trusted, no bearer)
/// </summary>
public static class Program
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
            .AddTeamsPhoneTools();

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
            });

        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .AddTeamsPhoneTools();

        var app = builder.Build();

        WarnIfNoToken(app);

        app.UseMiddleware<CorrelationLoggingMiddleware>();
        app.UseMiddleware<BearerAuthMiddleware>();

        app.MapMcp("/mcp");

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
