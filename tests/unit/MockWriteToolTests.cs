using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;
using TeamsPhoneMcp.Core.Tools;

namespace TeamsPhoneMcp.UnitTests;

public class MockWriteToolTests
{
    [Fact]
    public void MockWriteUserPolicy_CompletesTwoStepFlow()
    {
        var manifest = new ToolManifest
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
                ["tenantId"] = new() { Type = "string", Required = true }
            },
            MaxBlastRadius = 1,
            TimeoutSeconds = 120
        };

        var catalog = new FakeCatalog(manifest);
        var engine = new WritePolicyEngine(new ConfirmationTokenService(new byte[32], TimeSpan.FromMinutes(15)));
        var tool = new MockWriteTool(catalog, engine);

        var dryRun = tool.MockWriteUserPolicy("tenant-a", "alex@contoso.com", "Global", dryRun: null);
        Assert.Equal("dryRunCompleted", dryRun.Status);
        Assert.False(string.IsNullOrWhiteSpace(dryRun.ConfirmationToken));

        var execute = tool.MockWriteUserPolicy(
            "tenant-a",
            "alex@contoso.com",
            "Global",
            dryRun: false,
            confirmationToken: dryRun.ConfirmationToken);

        Assert.Equal("succeeded", execute.Status);
    }

    private sealed class FakeCatalog(ToolManifest manifest) : IToolManifestCatalog
    {
        public IReadOnlyList<ToolManifest> All => [manifest];

        public ToolManifest GetRequired(string toolId) => manifest;
    }
}
