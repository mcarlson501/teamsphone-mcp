using System.Text.Json;
using TeamsPhoneMcp.Core.Manifests;

namespace TeamsPhoneMcp.Core.Policy;

public sealed class WritePolicyEngine(IConfirmationTokenService tokenService)
{
    public PolicyDecision Evaluate(ToolManifest manifest, WritePolicyRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var effectiveDryRun = ResolveDryRunFlag(request.DryRun, request.WhatIf);
        if (effectiveDryRun is null)
        {
            return PolicyDecision.Reject("invalidArguments", "dryRun and whatIf cannot conflict.");
        }

        if (request.BlastRadius < 1 || request.BlastRadius > manifest.MaxBlastRadius)
        {
            return PolicyDecision.Reject("blastRadiusExceeded", $"Blast radius must be between 1 and {manifest.MaxBlastRadius}.");
        }

        if (manifest.RiskTier > request.MaxRiskTier)
        {
            return PolicyDecision.Reject("tierGated", $"Tool risk tier {manifest.RiskTier} exceeds session maxRiskTier {request.MaxRiskTier}.");
        }

        if (manifest.RiskTier >= 3 && !request.AllowTier3)
        {
            return PolicyDecision.Reject("tier3NotAllowed", "allowTier3 must be true for tier-3 tools.");
        }

        if (manifest.RiskTier == 0)
        {
            return PolicyDecision.Execute();
        }

        if (request.SessionWhatIfMode)
        {
            return PolicyDecision.DryRun(null, simulated: true);
        }

        if (effectiveDryRun.Value)
        {
            var token = tokenService.Issue(manifest.Id, request.TenantId, request.CanonicalToolParameters, nowUtc);
            return PolicyDecision.DryRun(token, simulated: false);
        }

        var validation = tokenService.Validate(
            request.ConfirmationToken ?? string.Empty,
            manifest.Id,
            request.TenantId,
            request.CanonicalToolParameters,
            nowUtc);

        if (!validation.IsValid)
        {
            return PolicyDecision.Reject(validation.ErrorCode ?? "invalidConfirmationToken", "A valid confirmationToken is required.");
        }

        return PolicyDecision.Execute();
    }

    private static bool? ResolveDryRunFlag(bool? dryRun, bool? whatIf)
    {
        if (dryRun.HasValue && whatIf.HasValue && dryRun.Value != whatIf.Value)
        {
            return null;
        }

        return dryRun ?? whatIf ?? true;
    }
}

public sealed record WritePolicyRequest(
    string TenantId,
    JsonElement CanonicalToolParameters,
    bool? DryRun,
    bool? WhatIf,
    string? ConfirmationToken,
    int BlastRadius,
    bool AllowTier3,
    int MaxRiskTier,
    bool SessionWhatIfMode);

public sealed record PolicyDecision(bool Approved, bool IsDryRun, bool Simulated, string? ConfirmationToken, string? ErrorCode, string? ErrorMessage)
{
    public static PolicyDecision Execute() => new(true, false, false, null, null, null);

    public static PolicyDecision DryRun(string? token, bool simulated) => new(true, true, simulated, token, null, null);

    public static PolicyDecision Reject(string code, string message) => new(false, false, false, null, code, message);
}
