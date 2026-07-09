using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core.Tools;

namespace TeamsPhoneMcp.Core;

/// <summary>
/// Central tool-registration seam. M1 kickoff keeps registration centralized while
/// introducing the manifest catalog + policy services used by tool handlers.
/// </summary>
public static class ToolRegistration
{
    /// <summary>
    /// Registers every tool this server exposes onto the supplied MCP server builder.
    /// Registers currently available tools plus milestone-1 policy/manifest services.
    /// </summary>
    public static IMcpServerBuilder AddTeamsPhoneTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IConfirmationTokenService, ConfirmationTokenService>();
        builder.Services.AddSingleton<WritePolicyEngine>();
        builder.Services.AddSingleton<IToolManifestCatalog>(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ToolManifestCatalog>>();
            return new ToolManifestCatalog(Path.Combine(env.ContentRootPath, "tools"), logger);
        });

        builder.WithTools<PingTool>();
        builder.WithTools<MockWriteTool>();

        return builder;
    }
}
