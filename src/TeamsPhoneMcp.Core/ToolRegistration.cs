using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core.Tools;

namespace TeamsPhoneMcp.Core;

/// <summary>
/// Central tool-registration seam. Mirrors the reference template's
/// <c>registerAllTools()</c> pattern so later milestones (M1's manifest-driven
/// registry) can replace the hardcoded set here without touching the host wiring.
/// </summary>
public static class ToolRegistration
{
    /// <summary>
    /// Registers every tool this server exposes onto the supplied MCP server builder.
    /// For M0 this is only the trivial <c>ping</c> tool.
    /// </summary>
    public static IMcpServerBuilder AddTeamsPhoneTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithTools<PingTool>();

        return builder;
    }
}
