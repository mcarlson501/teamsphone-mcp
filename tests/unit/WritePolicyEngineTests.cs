using System.Text.Json;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;

namespace TeamsPhoneMcp.UnitTests;

public class WritePolicyEngineTests
{
    private static readonly ToolManifest TierTwoManifest = new()
    {
        Id = "mock-write-user-policy",
        Version = "1.0.0",
        Summary = "Mock",
        Category = "change",
        RiskTier = 2,
        TelephonyModels = ["callingPlans"],
        Annotations = new ToolManifestAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true },
        Inputs = new Dictionary<string, ToolManifestInput>
        {
            ["targetUserUpn"] = new() { Type = "string", Required = true }
        },
        MaxBlastRadius = 1,
        TimeoutSeconds = 120
    };

    [Fact]
    public void Evaluate_RequiresDryRunTokenFlow_ForTierOnePlus()
    {
        var now = DateTimeOffset.UtcNow;
        var tokenService = new ConfirmationTokenService(new byte[32], TimeSpan.FromMinutes(15));
        var engine = new WritePolicyEngine(tokenService);
        var parameters = JsonSerializer.SerializeToElement(new { targetUserUpn = "alex@contoso.com", policyName = "Global" });

        var dryRun = engine.Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", parameters, null, null, null, 1, false, 3, false),
            now);

        Assert.True(dryRun.Approved);
        Assert.True(dryRun.IsDryRun);
        Assert.False(string.IsNullOrWhiteSpace(dryRun.ConfirmationToken));

        var execute = engine.Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", parameters, false, null, dryRun.ConfirmationToken, 1, false, 3, false),
            now.AddMinutes(1));

        Assert.True(execute.Approved);
        Assert.False(execute.IsDryRun);
    }

    [Fact]
    public void Evaluate_RejectsWhenBlastRadiusExceedsManifest()
    {
        var tokenService = new ConfirmationTokenService(new byte[32], TimeSpan.FromMinutes(15));
        var engine = new WritePolicyEngine(tokenService);
        var parameters = JsonSerializer.SerializeToElement(new { targetUserUpn = "alex@contoso.com" });

        var result = engine.Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", parameters, true, null, null, 2, false, 3, false),
            DateTimeOffset.UtcNow);

        Assert.False(result.Approved);
        Assert.Equal("blastRadiusExceeded", result.ErrorCode);
    }
}
