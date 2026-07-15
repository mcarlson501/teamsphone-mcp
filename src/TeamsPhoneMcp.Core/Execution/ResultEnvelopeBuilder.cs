using System.Text.Json;

namespace TeamsPhoneMcp.Core.Execution;

/// <summary>Mutable accumulator the pipeline fills as stages complete.</summary>
internal sealed class PipelineExecutionState
{
    public JsonElement? Before { get; set; }

    public JsonElement? After { get; set; }

    public string? Summary { get; set; }

    public IReadOnlyList<ToolCheckResult>? Preflight { get; set; }

    public IReadOnlyList<ToolCheckResult>? Verification { get; set; }

    public Dictionary<string, long> Timings { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Centralizes construction of <see cref="ToolResultEnvelope"/> instances and the
/// final error-sanitization safety net, ensuring no raw exception text or tenant
/// identifiers leak into client-facing results.
/// </summary>
internal static class ResultEnvelopeBuilder
{
    public static ToolResultEnvelope Build(
        ToolPipelineRequest request,
        ToolExecutionStatus status,
        bool dryRun,
        string? confirmationToken,
        PipelineExecutionState state,
        long totalMs,
        ToolError? error)
    {
        ToolDiff? diff = state.Before is null && state.After is null
            ? null
            : new ToolDiff(state.Before, state.After);

        return new ToolResultEnvelope
        {
            Status = status,
            ToolId = request.Manifest.Id,
            ToolVersion = request.Manifest.Version,
            TenantId = request.SessionContext.TenantId,
            CorrelationId = request.CorrelationId,
            DryRun = dryRun,
            ConfirmationToken = confirmationToken,
            Summary = string.IsNullOrWhiteSpace(state.Summary) ? DefaultSummary(status) : state.Summary!,
            Diff = diff,
            Preflight = state.Preflight,
            Verification = state.Verification,
            Timings = new ToolTimings(totalMs, state.Timings),
            Error = error
        };
    }

    /// <summary>
    /// Produces a client-safe <see cref="ToolError"/>. Stage executors are expected
    /// to sanitize already; this is a defensive fallback for missing/blank values.
    /// </summary>
    public static ToolError Sanitize(string? errorCode, string? message) =>
        new(
            string.IsNullOrWhiteSpace(errorCode) ? StageErrorCodes.ExecutionFailed : errorCode!,
            string.IsNullOrWhiteSpace(message) ? "The tool stage failed." : message!);

    private static string DefaultSummary(ToolExecutionStatus status) => status switch
    {
        ToolExecutionStatus.Succeeded => "Operation completed.",
        ToolExecutionStatus.DryRunCompleted => "Dry-run completed.",
        ToolExecutionStatus.PreflightFailed => "Preflight checks failed.",
        ToolExecutionStatus.RolledBack => "Operation failed and was rolled back.",
        ToolExecutionStatus.VerifyFailedRolledBack => "Verification failed and the change was rolled back.",
        _ => "Operation failed."
    };
}
