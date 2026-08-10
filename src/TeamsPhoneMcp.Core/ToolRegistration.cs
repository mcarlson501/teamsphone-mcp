using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Reflection;
using ModelContextProtocol.Protocol;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;
using TeamsPhoneMcp.Core.Sessions;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Audit;
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

        builder.Services.AddSingleton(CreateTokenSigningKey);
        builder.Services.AddSingleton<ConsumedConfirmationTokenCache>();
        builder.Services.AddSingleton<IConfirmationTokenService>(sp =>
            new ConfirmationTokenService(
                sp.GetRequiredService<TokenSigningKey>().Value,
                TimeSpan.FromMinutes(15),
                sp.GetRequiredService<ConsumedConfirmationTokenCache>()));
        builder.Services.AddSingleton<IContinuationTokenService>(sp =>
            new ContinuationTokenService(
                sp.GetRequiredService<TokenSigningKey>().Value,
                TimeSpan.FromMinutes(30)));
        builder.Services.AddSingleton<ToolPaginationResolver>();
        builder.Services.AddSingleton<WritePolicyEngine>();
        builder.Services.AddSingleton<McpSessionPolicyStore>();
        builder.Services.AddSingleton<IMcpSessionPolicyStore>(services =>
            services.GetRequiredService<McpSessionPolicyStore>());
        builder.Services.AddSingleton<McpSessionOwnershipStore>();
        builder.Services.AddSingleton<AuthenticatedClientAccessor>();
        builder.Services.AddSingleton<IAuthenticatedClientAccessor>(services =>
            services.GetRequiredService<AuthenticatedClientAccessor>());
        builder.Services.AddSingleton<McpRequestSessionAccessor>();
        builder.Services.AddSingleton<IMcpRequestSessionAccessor>(services =>
            services.GetRequiredService<McpRequestSessionAccessor>());
        builder.Services.Configure<McpServerOptions>(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = options.ServerInfo?.Name ?? "TeamsPhoneMcp.Host",
                Version = GetProductVersion(),
            };
            options.Filters.Message.IncomingFilters.Add(McpSessionPolicyStore.CreateInitializationFilter());
        });
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
        builder.Services.TryAddSingleton<IGraphCallRecordsClient, UnconfiguredGraphCallRecordsClient>();

        // Fail-safe audit defaults: the host swaps these for the JSONL pipeline
        // via AddTeamsPhoneAudit, so unit tests never touch the filesystem.
        builder.Services.TryAddSingleton<IAuditSink, NullAuditSink>();
        builder.Services.TryAddSingleton<IAuditSnapshotStore, NullAuditSnapshotStore>();
        builder.Services.TryAddSingleton<IAuditQueryService, NullAuditQueryService>();
        builder.Services.TryAddSingleton<IToolAuditRecorder, ToolAuditRecorder>();
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

    private static string GetProductVersion()
    {
        var informationalVersion = typeof(ToolRegistration).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return informationalVersion?.Split('+', 2)[0]
            ?? throw new InvalidOperationException("The product version is missing from the Core assembly.");
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
        builder.Services.RemoveAll<IGraphCallRecordsClient>();
        builder.Services.AddSingleton<IGraphCallRecordsClient, GraphCallRecordsClient>();

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
            McpServerTool? localAuditTool = manifest.Id switch
            {
                QueryAuditLogMcpServerTool.ToolId => new QueryAuditLogMcpServerTool(manifest),
                GetChangeDetailMcpServerTool.ToolId => new GetChangeDetailMcpServerTool(manifest),
                ExportAuditReportMcpServerTool.ToolId => new ExportAuditReportMcpServerTool(manifest),
                ExportAuditReportMcpServerTool.ReportChangeHistoryToolId => new ExportAuditReportMcpServerTool(manifest),
                GetPstnUsageMcpServerTool.ToolId => new GetPstnUsageMcpServerTool(manifest),
                GetCallQualitySummaryMcpServerTool.ToolId => new GetCallQualitySummaryMcpServerTool(manifest),
                _ => null,
            };
            if (localAuditTool is not null)
            {
                services.AddSingleton<McpServerTool>(
                    new ManifestValidatingMcpServerTool(localAuditTool));
                continue;
            }

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

    private static TokenSigningKey CreateTokenSigningKey(IServiceProvider services)
    {
        var configuration = services.GetService<IConfiguration>();
        var logger = services.GetRequiredService<ILogger<ConfirmationTokenService>>();
        var keyFromConfig =
            configuration?["TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY"] ??
            configuration?["Policy:ConfirmationTokenKey"];

        if (!string.IsNullOrWhiteSpace(keyFromConfig))
        {
            try
            {
                var key = Convert.FromBase64String(keyFromConfig);
                if (key.Length < 32)
                {
                    throw new ArgumentException("Token signing key must be at least 32 bytes.", nameof(keyFromConfig));
                }

                return new TokenSigningKey(key);
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
            "No persistent token signing key configured. Generated an ephemeral key for this process. " +
            "Set TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY to keep signed tokens valid across restarts.");
        return new TokenSigningKey(Convert.FromBase64String(ConfirmationTokenService.CreateRandomBase64Key()));
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

    private sealed record TokenSigningKey(byte[] Value);
}
