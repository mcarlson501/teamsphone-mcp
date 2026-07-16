using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TeamsPhoneMcp.Core.Tools;

/// <summary>
/// Resolves the server-level execution mode ceiling (build spec §6.4 rule 7) from
/// configuration. <c>TEAMSPHONE_MCP_MODE</c> (or <c>ServerMode</c>) accepts
/// <c>full</c> (default), <c>whatif</c>, or <c>readonly</c>.
/// </summary>
internal static class ServerModeCeiling
{
    public enum Mode
    {
        /// <summary>Writes execute normally after the two-step confirmation flow.</summary>
        Full,

        /// <summary>Every write is forced to simulate; no confirmation token is issued.</summary>
        WhatIf,

        /// <summary>Only tier-0 read tools are permitted.</summary>
        ReadOnly,
    }

    public static Mode Resolve(IServiceProvider services)
    {
        var configuration = services.GetService<IConfiguration>();
        var raw = configuration?["TEAMSPHONE_MCP_MODE"] ?? configuration?["ServerMode"];

        return raw?.Trim().ToLowerInvariant() switch
        {
            "whatif" => Mode.WhatIf,
            "readonly" => Mode.ReadOnly,
            _ => Mode.Full,
        };
    }
}
