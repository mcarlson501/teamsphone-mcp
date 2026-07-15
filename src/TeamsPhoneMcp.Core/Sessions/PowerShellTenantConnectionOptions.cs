namespace TeamsPhoneMcp.Core.Sessions;

/// <summary>
/// Options controlling how a tenant runspace imports the Teams module and
/// connects. Bound from the <c>PowerShellTenantSession</c> configuration section.
/// </summary>
public sealed class PowerShellTenantConnectionOptions
{
    public const string SectionName = "PowerShellTenantSession";

    /// <summary>PowerShell module that provides the Teams admin cmdlets.</summary>
    public string ModuleName { get; set; } = "MicrosoftTeams";

    /// <summary>
    /// Optional pinned module version. When set, the runspace imports exactly this
    /// version (build spec §3: the module version is pinned in the container image).
    /// </summary>
    public string? ModuleRequiredVersion { get; set; }
}
