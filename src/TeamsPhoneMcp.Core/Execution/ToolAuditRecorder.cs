using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeamsPhoneMcp.Audit;
using TeamsPhoneMcp.Core.Manifests;

namespace TeamsPhoneMcp.Core.Execution;

/// <summary>Per-call context the audit trail needs but the result envelope does not carry.</summary>
public sealed record ToolAuditContext(
    ToolManifest Manifest,
    string CorrelationId,
    string TenantId,
    JsonElement Parameters,
    bool Simulated)
{
    /// <summary>MCP session the call arrived on, when the transport exposes one.</summary>
    public string? SessionId { get; init; }

    /// <summary>Reported client identity (name/version), used to attribute changes.</summary>
    public string? ClientId { get; init; }
}

/// <summary>
/// Turns a completed tool call into exactly one audit record (build spec §9.1).
/// Every execution path funnels through here — successes, dry-runs, policy
/// rejections and failures alike — so a missing record always means a bug, not
/// an uninteresting call.
/// </summary>
public interface IToolAuditRecorder
{
    Task RecordAsync(ToolAuditContext context, ToolResultEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ToolAuditRecorder : IToolAuditRecorder
{
    private readonly IAuditSink _sink;
    private readonly IAuditSnapshotStore _snapshotStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ToolAuditRecorder> _logger;

    public ToolAuditRecorder(
        IAuditSink sink,
        IAuditSnapshotStore snapshotStore,
        TimeProvider timeProvider,
        ILogger<ToolAuditRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sink = sink;
        _snapshotStore = snapshotStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RecordAsync(
        ToolAuditContext context,
        ToolResultEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        try
        {
            var timestamp = _timeProvider.GetUtcNow();

            var snapshotRefs = await _snapshotStore.StoreAsync(
                context.TenantId,
                context.CorrelationId,
                timestamp,
                envelope.Diff?.Before,
                envelope.Diff?.After,
                cancellationToken).ConfigureAwait(false);

            var record = new AuditRecord
            {
                Timestamp = timestamp,
                CorrelationId = context.CorrelationId,
                SessionId = context.SessionId,
                ClientId = context.ClientId,
                TenantId = context.TenantId,
                ToolId = context.Manifest.Id,
                ToolVersion = context.Manifest.Version,
                Status = envelope.Status.ToString(),
                ErrorCode = envelope.Error?.Code,
                ErrorMessage = AuditRedactor.ScrubText(envelope.Error?.Message),
                DryRun = envelope.DryRun,
                Simulated = context.Simulated,
                RiskTier = context.Manifest.RiskTier,
                Parameters = AuditRedactor.Redact(context.Parameters, context.Manifest.RedactParams),
                Stages = BuildStages(envelope),
                Checks = BuildChecks(envelope),
                DurationMs = envelope.Timings?.TotalMs ?? 0,
                SnapshotRefs = snapshotRefs,
            };

            await _sink.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broken audit trail must not turn a completed tenant operation
            // into a client-visible failure; the gap is loud in the logs instead.
            _logger.LogError(
                ex,
                "Failed to record the audit entry for tool {ToolId} (correlation {CorrelationId}).",
                context.Manifest.Id,
                context.CorrelationId);
        }
    }

    private static IReadOnlyList<AuditStageTiming> BuildStages(ToolResultEnvelope envelope)
    {
        if (envelope.Timings?.Stages is not { Count: > 0 } stages)
        {
            return Array.Empty<AuditStageTiming>();
        }

        return stages.Select(pair => new AuditStageTiming(pair.Key, pair.Value)).ToArray();
    }

    private static IReadOnlyList<AuditCheckResult> BuildChecks(ToolResultEnvelope envelope)
    {
        var checks = new List<AuditCheckResult>();

        foreach (var check in envelope.Preflight ?? Array.Empty<ToolCheckResult>())
        {
            checks.Add(new AuditCheckResult("preflight", check.Check, check.Passed, AuditRedactor.ScrubText(check.Detail)));
        }

        foreach (var check in envelope.Verification ?? Array.Empty<ToolCheckResult>())
        {
            checks.Add(new AuditCheckResult("verify", check.Check, check.Passed, AuditRedactor.ScrubText(check.Detail)));
        }

        return checks;
    }
}
