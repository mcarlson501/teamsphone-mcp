using System.Text.Json;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;

namespace TeamsPhoneMcp.UnitTests;

public class WritePolicyEngineTests
{
    private static readonly ToolManifest TierZeroManifest = CreateManifest("read-tool", "read", 0, 0);
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
    private static readonly ToolManifest TierThreeManifest = CreateManifest("tier-three-tool", "delete", 3, 1);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void Evaluate_RequiresDryRunTokenFlow_ForTierOnePlus()
    {
        var engine = CreateEngine();
        var parameters = CreateParameters();

        var dryRun = engine.Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", parameters, null, null, null, 1, false, 3, false),
            Now);

        Assert.True(dryRun.Approved);
        Assert.True(dryRun.IsDryRun);
        Assert.False(string.IsNullOrWhiteSpace(dryRun.ConfirmationToken));

        var execute = engine.Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", parameters, false, null, dryRun.ConfirmationToken, 1, false, 3, false),
            Now.AddMinutes(1));

        Assert.True(execute.Approved);
        Assert.False(execute.IsDryRun);
    }

    [Fact]
    public void Evaluate_TierZeroExecutesWithoutWriteSafetyFlow()
    {
        var result = CreateEngine().Evaluate(
            TierZeroManifest,
            new WritePolicyRequest("tenant-a", CreateParameters(), false, null, null, 0, false, 0, false),
            Now);

        Assert.True(result.Approved);
        Assert.False(result.IsDryRun);
        Assert.Null(result.ConfirmationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Evaluate_RejectsBlastRadiusOutsideManifestBounds(int blastRadius)
    {
        var result = CreateEngine().Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", CreateParameters(), true, null, null, blastRadius, false, 3, false),
            Now);

        Assert.False(result.Approved);
        Assert.Equal("blastRadiusExceeded", result.ErrorCode);
    }

    [Fact]
    public void Evaluate_AllowsBlastRadiusAtManifestMaximum()
    {
        var result = CreateEngine().Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", CreateParameters(), true, null, null, 1, false, 3, false),
            Now);

        Assert.True(result.Approved);
        Assert.True(result.IsDryRun);
    }

    [Fact]
    public void Evaluate_RejectsRiskTierAboveSessionCeiling()
    {
        var result = CreateEngine().Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", CreateParameters(), true, null, null, 1, false, 1, false),
            Now);

        Assert.False(result.Approved);
        Assert.Equal("tierGated", result.ErrorCode);
    }

    [Fact]
    public void Evaluate_RequiresTierThreeOptIn()
    {
        var result = CreateEngine().Evaluate(
            TierThreeManifest,
            new WritePolicyRequest("tenant-a", CreateParameters(), true, null, null, 1, false, 3, false),
            Now);

        Assert.False(result.Approved);
        Assert.Equal("tier3NotAllowed", result.ErrorCode);
    }

    [Fact]
    public void Evaluate_AllowsTierThreeDryRunWithOptIn()
    {
        var result = CreateEngine().Evaluate(
            TierThreeManifest,
            new WritePolicyRequest("tenant-a", CreateParameters(), true, null, null, 1, true, 3, false),
            Now);

        Assert.True(result.Approved);
        Assert.True(result.IsDryRun);
        Assert.NotNull(result.ConfirmationToken);
    }

    [Fact]
    public void Evaluate_RejectsConflictingDryRunAliases()
    {
        var result = CreateEngine().Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", CreateParameters(), true, false, null, 1, false, 3, false),
            Now);

        Assert.False(result.Approved);
        Assert.Equal("invalidArguments", result.ErrorCode);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(true, null)]
    [InlineData(null, true)]
    [InlineData(true, true)]
    public void Evaluate_DryRunAliasesDefaultToSafeSimulation(bool? dryRun, bool? whatIf)
    {
        var result = CreateEngine().Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", CreateParameters(), dryRun, whatIf, null, 1, false, 3, false),
            Now);

        Assert.True(result.Approved);
        Assert.True(result.IsDryRun);
        Assert.False(result.Simulated);
        Assert.NotNull(result.ConfirmationToken);
    }

    [Fact]
    public void Evaluate_SessionWhatIfModeCannotExecuteOrIssueToken()
    {
        var result = CreateEngine().Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", CreateParameters(), false, null, "ignored", 1, false, 3, true),
            Now);

        Assert.True(result.Approved);
        Assert.True(result.IsDryRun);
        Assert.True(result.Simulated);
        Assert.Null(result.ConfirmationToken);
    }

    [Fact]
    public void Evaluate_RejectsMissingConfirmationTokenForExecute()
    {
        var result = CreateEngine().Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", CreateParameters(), false, null, null, 1, false, 3, false),
            Now);

        Assert.False(result.Approved);
        Assert.Equal("missingConfirmationToken", result.ErrorCode);
    }

    [Fact]
    public void Evaluate_RejectsExpiredConfirmationToken()
    {
        var engine = CreateEngine();
        var parameters = CreateParameters();
        var dryRun = engine.Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", parameters, true, null, null, 1, false, 3, false),
            Now);

        var result = engine.Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", parameters, false, null, dryRun.ConfirmationToken, 1, false, 3, false),
            Now.AddMinutes(15));

        Assert.False(result.Approved);
        Assert.Equal("expiredConfirmationToken", result.ErrorCode);
    }

    [Theory]
    [InlineData("tenant-b", "mock-write-user-policy", "Changed")]
    [InlineData("tenant-a", "other-write-tool", "Global")]
    [InlineData("tenant-a", "mock-write-user-policy", "Changed")]
    public void Evaluate_RejectsTokenBoundToDifferentContext(
        string tenantId,
        string toolId,
        string policyName)
    {
        var engine = CreateEngine();
        var issuedParameters = CreateParameters();
        var dryRun = engine.Evaluate(
            TierTwoManifest,
            new WritePolicyRequest("tenant-a", issuedParameters, true, null, null, 1, false, 3, false),
            Now);
        var manifest = toolId == TierTwoManifest.Id
            ? TierTwoManifest
            : CreateManifest(toolId, "change", 2, 1);
        var executeParameters = JsonSerializer.SerializeToElement(new
        {
            targetUserUpn = "alex@contoso.com",
            policyName
        });

        var result = engine.Evaluate(
            manifest,
            new WritePolicyRequest(tenantId, executeParameters, false, null, dryRun.ConfirmationToken, 1, false, 3, false),
            Now.AddMinutes(1));

        Assert.False(result.Approved);
        Assert.Equal("invalidConfirmationToken", result.ErrorCode);
    }

    private static WritePolicyEngine CreateEngine() =>
        new(new ConfirmationTokenService(new byte[32], TimeSpan.FromMinutes(15)));

    private static JsonElement CreateParameters() =>
        JsonSerializer.SerializeToElement(new
        {
            targetUserUpn = "alex@contoso.com",
            policyName = "Global"
        });

    private static ToolManifest CreateManifest(
        string id,
        string category,
        int riskTier,
        int maxBlastRadius) => new()
        {
            Id = id,
            Version = "1.0.0",
            Summary = "Test manifest",
            Category = category,
            RiskTier = riskTier,
            Annotations = new ToolManifestAnnotations
            {
                ReadOnlyHint = riskTier == 0,
                DestructiveHint = riskTier == 3,
                IdempotentHint = true
            },
            Inputs = new Dictionary<string, ToolManifestInput>
            {
                ["targetUserUpn"] = new() { Type = "string", Required = true }
            },
            MaxBlastRadius = maxBlastRadius,
            TimeoutSeconds = 30
        };
}
