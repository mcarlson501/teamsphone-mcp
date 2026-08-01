namespace TeamsPhoneMcp.Core.Tools;

/// <summary>
/// Argument names the host owns for tenant context and write-safety policy. They
/// are stripped from the business payload so the confirmation-token hash stays
/// stable and the audit trail records only the tool's own parameters.
/// </summary>
public static class ReservedToolArguments
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "tenantId",
        "credentialRef",
        "dryRun",
        "whatIf",
        "confirmationToken",
        "blastRadius",
        "allowTier3",
        "maxRiskTier",
        "pageSize",
        "continuationToken",
    };
}
