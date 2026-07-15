using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamsPhoneMcp.Core.Execution;

/// <summary>Terminal outcome of a tool pipeline run (build spec §6.3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolExecutionStatus
{
    Succeeded,
    DryRunCompleted,
    PreflightFailed,
    Failed,
    RolledBack,
    VerifyFailedRolledBack
}

/// <summary>
/// Structured, client-safe result of a tool pipeline run. Optional members are
/// populated only when the corresponding stage produced data.
/// </summary>
public sealed record ToolResultEnvelope
{
    /// <summary>Schema version for forward compatibility.</summary>
    public int EnvelopeVersion { get; init; } = 1;

    public required ToolExecutionStatus Status { get; init; }

    public required string ToolId { get; init; }

    public required string ToolVersion { get; init; }

    public required Guid TenantId { get; init; }

    public required string CorrelationId { get; init; }

    public required bool DryRun { get; init; }

    /// <summary>Present only on <see cref="ToolExecutionStatus.DryRunCompleted"/>.</summary>
    public string? ConfirmationToken { get; init; }

    public required string Summary { get; init; }

    public ToolDiff? Diff { get; init; }

    public IReadOnlyList<ToolCheckResult>? Preflight { get; init; }

    public IReadOnlyList<ToolCheckResult>? Verification { get; init; }

    public ToolTimings? Timings { get; init; }

    public ToolError? Error { get; init; }
}

/// <summary>Before/after tenant state captured from snapshot and result stages.</summary>
public sealed record ToolDiff(JsonElement? Before, JsonElement? After);

/// <summary>A single preflight or verification check outcome.</summary>
public sealed record ToolCheckResult(string Check, bool Passed, string? Detail);

/// <summary>Per-stage and total wall-clock timings in milliseconds.</summary>
public sealed record ToolTimings(long TotalMs, IReadOnlyDictionary<string, long> Stages);

/// <summary>Sanitized error surfaced to the client.</summary>
public sealed record ToolError(string Code, string Message);
