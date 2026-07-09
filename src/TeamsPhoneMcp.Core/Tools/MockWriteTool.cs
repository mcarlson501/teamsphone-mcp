using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;

namespace TeamsPhoneMcp.Core.Tools;

[McpServerToolType]
public sealed class MockWriteTool(IToolManifestCatalog manifestCatalog, WritePolicyEngine policyEngine)
{
    private const string ToolId = "mock-write-user-policy";

    [McpServerTool(Name = ToolId, ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Milestone-1 mock write tool used to demonstrate dry-run plus confirmation-token execution flow.")]
    public MockWriteResult MockWriteUserPolicy(
        [Description("Target tenant ID for the mock operation.")] string tenantId,
        [Description("Target user UPN.")] string targetUserUpn,
        [Description("Policy name to apply to the user.")] string policyName,
        [Description("Execute mode. Omit or true for dry-run; set false with confirmationToken to execute.")]
        bool? dryRun = null,
        [Description("Alias for dryRun, matching PowerShell WhatIf semantics.")] bool? whatIf = null,
        [Description("Confirmation token issued by a prior dry-run call.")] string? confirmationToken = null,
        [Description("Declared number of entities affected by this call.")] int blastRadius = 1)
    {
        var manifest = manifestCatalog.GetRequired(ToolId);
        var correlationId = Guid.NewGuid().ToString();

        var toolParams = JsonSerializer.SerializeToElement(new
        {
            targetUserUpn,
            policyName,
            blastRadius
        });

        var decision = policyEngine.Evaluate(
            manifest,
            new WritePolicyRequest(
                tenantId,
                toolParams,
                dryRun,
                whatIf,
                confirmationToken,
                blastRadius,
                AllowTier3: false,
                MaxRiskTier: 3,
                SessionWhatIfMode: false),
            DateTimeOffset.UtcNow);

        if (!decision.Approved)
        {
            return new MockWriteResult(
                Status: "policyRejected",
                ToolId: manifest.Id,
                ToolVersion: manifest.Version,
                TenantId: tenantId,
                CorrelationId: correlationId,
                DryRun: false,
                Simulated: false,
                ConfirmationToken: null,
                Summary: $"Blocked by policy for {targetUserUpn}.",
                ErrorCode: decision.ErrorCode,
                ErrorMessage: decision.ErrorMessage);
        }

        if (decision.IsDryRun)
        {
            return new MockWriteResult(
                Status: "dryRunCompleted",
                ToolId: manifest.Id,
                ToolVersion: manifest.Version,
                TenantId: tenantId,
                CorrelationId: correlationId,
                DryRun: true,
                Simulated: decision.Simulated,
                ConfirmationToken: decision.ConfirmationToken,
                Summary: $"Dry-run validated update of {targetUserUpn} to policy '{policyName}'.",
                ErrorCode: null,
                ErrorMessage: null);
        }

        return new MockWriteResult(
            Status: "succeeded",
            ToolId: manifest.Id,
            ToolVersion: manifest.Version,
            TenantId: tenantId,
            CorrelationId: correlationId,
            DryRun: false,
            Simulated: false,
            ConfirmationToken: null,
            Summary: $"Applied policy '{policyName}' to {targetUserUpn}.",
            ErrorCode: null,
            ErrorMessage: null);
    }
}

public sealed record MockWriteResult(
    string Status,
    string ToolId,
    string ToolVersion,
    string TenantId,
    string CorrelationId,
    bool DryRun,
    bool Simulated,
    string? ConfirmationToken,
    string Summary,
    string? ErrorCode,
    string? ErrorMessage);
