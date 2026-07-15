using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;
using TeamsPhoneMcp.Core.Sessions;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core.Tools;

namespace TeamsPhoneMcp.Core;

/// <summary>
/// Central registration for manifest-validated tools and their policy services.
/// </summary>
public static class ToolRegistration
{
    /// <summary>
    /// Registers every tool this server exposes onto the supplied MCP server builder.
    /// Registers the current tools plus policy and manifest services.
    /// </summary>
    public static IMcpServerBuilder AddTeamsPhoneTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IConfirmationTokenService>(sp =>
        {
            var configuration = sp.GetService<IConfiguration>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConfirmationTokenService>>();
            var keyFromConfig =
                configuration?["TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY"] ??
                configuration?["Policy:ConfirmationTokenKey"];

            if (!string.IsNullOrWhiteSpace(keyFromConfig))
            {
                try
                {
                    return ConfirmationTokenService.FromBase64Key(keyFromConfig, TimeSpan.FromMinutes(15));
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    throw new InvalidOperationException(
                        "TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY / Policy:ConfirmationTokenKey is set but invalid. " +
                        "Provide a valid Base64-encoded key of at least 32 bytes. " +
                        "Use ConfirmationTokenService.CreateRandomBase64Key() to generate one.",
                        ex);
                }
            }

            logger.LogWarning(
                "No persistent confirmation token key configured. Generated an ephemeral key for this process. " +
                "Set TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY to keep confirmation tokens valid across restarts.");
            return ConfirmationTokenService.FromBase64Key(ConfirmationTokenService.CreateRandomBase64Key(), TimeSpan.FromMinutes(15));
        });
        builder.Services.AddSingleton<WritePolicyEngine>();
        builder.Services.AddSingleton<IToolManifestCatalog>(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var configuration = sp.GetService<IConfiguration>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ToolManifestCatalog>>();
            var configuredPath = configuration?["ToolManifests:ToolsRootPath"];
            var toolsPath = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(AppContext.BaseDirectory, "tools")
                : Path.GetFullPath(configuredPath, env.ContentRootPath);
            return new ToolManifestCatalog(toolsPath, logger);
        });
        builder.Services.AddHostedService<ManifestCatalogStartupValidator>();
        builder.Services
            .AddOptions<TenantSessionOptions>()
            .BindConfiguration(TenantSessionOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<TenantSessionOptions>, TenantSessionOptionsValidator>();
        builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.TryAddSingleton<ITenantSessionFactory, UnconfiguredTenantSessionFactory>();
        builder.Services.AddSingleton<TenantSessionManager>();
        builder.Services.AddSingleton<ITenantSessionManager>(
            services => services.GetRequiredService<TenantSessionManager>());
        builder.Services.AddHostedService<TenantSessionCleanupService>();
        builder.Services.TryAddSingleton<IStageExecutor, UnconfiguredStageExecutor>();
        builder.Services.AddSingleton<IToolPipelineRunner, ToolPipelineRunner>();

        builder.WithTools(
        [
            CreateManifestValidatedTool<PingTool>(nameof(PingTool.Ping)),
            CreateManifestValidatedTool<MockWriteTool>(nameof(MockWriteTool.MockWriteUserPolicy))
        ]);

        return builder;
    }

    private static McpServerTool CreateManifestValidatedTool<TTool>(string methodName)
    {
        var method = typeof(TTool).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException(
                $"Could not find MCP tool method '{typeof(TTool).FullName}.{methodName}'.");

        var innerTool = method.IsStatic
            ? McpServerTool.Create(method, target: null)
            : McpServerTool.Create(
                method,
                request => ActivatorUtilities.CreateInstance(
                    request.Services
                        ?? throw new InvalidOperationException("The MCP tool request does not provide a service provider."),
                    typeof(TTool)));

        return new ManifestValidatingMcpServerTool(innerTool);
    }
}
