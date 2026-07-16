using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Reflection;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;
using TeamsPhoneMcp.Core.Sessions;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core.Tools;
using TeamsPhoneMcp.Credentials;

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
            return new ToolManifestCatalog(ResolveToolsRootPath(env, configuration), logger);
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
        builder.Services.TryAddSingleton<IToolPipelineRunner, ToolPipelineRunner>();
        builder.Services
            .AddOptions<PowerShellTenantConnectionOptions>()
            .BindConfiguration(PowerShellTenantConnectionOptions.SectionName);

        builder.WithTools(
        [
            CreateManifestValidatedTool<PingTool>(nameof(PingTool.Ping)),
            CreateManifestValidatedTool<MockWriteTool>(nameof(MockWriteTool.MockWriteUserPolicy))
        ]);

        RegisterManifestPipelineTools(builder.Services);

        return builder;
    }

    /// <summary>
    /// Opt-in registration of the self-host credential provider and the real
    /// certificate-authenticated PowerShell tenant session factory. Kept out of
    /// <see cref="AddTeamsPhoneTools"/> so the default posture (and every unit
    /// test) stays fail-closed with the <see cref="UnconfiguredTenantSessionFactory"/>.
    /// </summary>
    public static IMcpServerBuilder AddLocalTenantCredentials(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ICredentialProvider, LocalCredentialProvider>();
        builder.Services.RemoveAll<ITenantSessionFactory>();
        builder.Services.AddSingleton<ITenantSessionFactory, PowerShellTenantSessionFactory>();

        return builder;
    }

    /// <summary>
    /// Registers a manifest-driven <see cref="ManifestPipelineTool"/> for every
    /// tool folder that ships both a <c>manifest.yaml</c> and a <c>run.ps1</c>
    /// (excluding the <c>_template</c>). Tools with hand-written C# handlers
    /// (ping, mock-write) intentionally have no <c>run.ps1</c> and so are not
    /// double-registered here. Manifests are parsed eagerly so the tool's
    /// protocol contract is available without the DI catalog; any manifest
    /// problems are surfaced authoritatively by the startup validator.
    /// </summary>
    private static void RegisterManifestPipelineTools(IServiceCollection services)
    {
        var toolsRoot = Path.Combine(AppContext.BaseDirectory, "tools");
        if (!Directory.Exists(toolsRoot))
        {
            return;
        }

        ToolManifestCatalog catalog;
        try
        {
            catalog = new ToolManifestCatalog(toolsRoot, NullLogger<ToolManifestCatalog>.Instance);
        }
        catch (InvalidOperationException)
        {
            // Missing/invalid manifests are reported by ManifestCatalogStartupValidator.
            return;
        }

        foreach (var manifest in catalog.All)
        {
            var scriptPath = Path.Combine(toolsRoot, manifest.Id, ToolScriptLocator.ScriptFileName);
            if (!File.Exists(scriptPath))
            {
                continue;
            }

            var toolManifest = manifest;
            services.AddSingleton<McpServerTool>(_ => new ManifestPipelineTool(toolManifest));
        }
    }

    /// <summary>
    /// Opt-in replacement of the fail-closed <see cref="UnconfiguredStageExecutor"/>
    /// with the in-process PowerShell <see cref="RunspaceStageExecutor"/>. Kept
    /// separate so the default posture stays fail-closed; a real tenant session
    /// factory (which supplies the connected runspace) is still required before
    /// any stage can run.
    /// </summary>
    public static IMcpServerBuilder AddPowerShellStageExecution(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var configuration = sp.GetService<IConfiguration>();
            return new ToolScriptLocator(ResolveToolsRootPath(env, configuration));
        });
        builder.Services.RemoveAll<IStageExecutor>();
        builder.Services.AddSingleton<IStageExecutor, RunspaceStageExecutor>();

        return builder;
    }

    private static string ResolveToolsRootPath(IHostEnvironment env, IConfiguration? configuration)
    {
        var configuredPath = configuration?["ToolManifests:ToolsRootPath"];
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "tools")
            : Path.GetFullPath(configuredPath, env.ContentRootPath);
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
