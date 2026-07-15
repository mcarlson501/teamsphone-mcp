using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;
using TeamsPhoneMcp.Core.Sessions;

namespace TeamsPhoneMcp.Core.Execution;

/// <summary>Inputs required to run a tool through the staged execution pipeline.</summary>
public sealed record ToolPipelineRequest(
    ToolManifest Manifest,
    string CanonicalInputJson,
    TenantSessionContext SessionContext,
    PolicyDecision Decision,
    string CorrelationId);

/// <summary>
/// Orchestrates the staged execution of a tool (build spec §6.2–6.3) inside a
/// tenant-isolated session, returning a structured <see cref="ToolResultEnvelope"/>.
/// </summary>
public interface IToolPipelineRunner
{
    Task<ToolResultEnvelope> ExecuteAsync(ToolPipelineRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Default pipeline runner. Derives the read/write operation kind from the
/// manifest and policy decision (never from client input), runs the whole stage
/// sequence inside a single <see cref="ITenantSessionManager.ExecuteAsync{TResult}"/>
/// call so the session never escapes, threads the captured snapshot into later
/// stages, and gates rollback on risk tier.
/// </summary>
public sealed class ToolPipelineRunner : IToolPipelineRunner
{
    /// <summary>Rollback is only attempted for tools at this risk tier or higher.</summary>
    private const int RollbackMinimumRiskTier = 2;

    private readonly ITenantSessionManager _sessionManager;
    private readonly IStageExecutor _stageExecutor;
    private readonly ILogger<ToolPipelineRunner> _logger;

    public ToolPipelineRunner(
        ITenantSessionManager sessionManager,
        IStageExecutor stageExecutor,
        ILogger<ToolPipelineRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(stageExecutor);
        ArgumentNullException.ThrowIfNull(logger);

        _sessionManager = sessionManager;
        _stageExecutor = stageExecutor;
        _logger = logger;
    }

    public async Task<ToolResultEnvelope> ExecuteAsync(ToolPipelineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Defensive: a rejected policy decision should be handled before the runner.
        if (!request.Decision.Approved)
        {
            return ResultEnvelopeBuilder.Build(
                request,
                ToolExecutionStatus.Failed,
                dryRun: false,
                confirmationToken: null,
                new PipelineExecutionState(),
                totalMs: 0,
                ResultEnvelopeBuilder.Sanitize(request.Decision.ErrorCode, request.Decision.ErrorMessage));
        }

        var operationKind = DeriveOperationKind(request);

        try
        {
            return await _sessionManager.ExecuteAsync(
                request.SessionContext,
                operationKind,
                (session, innerCt) => RunPipelineAsync(session, request, innerCt),
                cancellationToken);
        }
        catch (TenantSessionFatalException ex)
        {
            _logger.LogWarning("Tool {ToolId} failed with a fatal tenant session error.", request.Manifest.Id);
            return FailedEnvelope(request, ex.ErrorCode, "The tenant session failed and was reset.");
        }
        catch (TenantSessionException ex)
        {
            _logger.LogWarning("Tool {ToolId} could not acquire a tenant session.", request.Manifest.Id);
            return FailedEnvelope(request, ex.ErrorCode, "The tenant session is unavailable.");
        }
    }

    private static TenantOperationKind DeriveOperationKind(ToolPipelineRequest request) =>
        request.Manifest.RiskTier == 0 || request.Decision.IsDryRun
            ? TenantOperationKind.Read
            : TenantOperationKind.Write;

    private async Task<ToolResultEnvelope> RunPipelineAsync(
        ITenantExecutionSession session,
        ToolPipelineRequest request,
        CancellationToken cancellationToken)
    {
        var state = new PipelineExecutionState();
        var overall = Stopwatch.StartNew();

        PipelineOutcome outcome;
        if (request.Manifest.RiskTier == 0)
        {
            outcome = await RunReadAsync(session, request, state, cancellationToken);
        }
        else if (request.Decision.IsDryRun)
        {
            outcome = await RunDryRunAsync(session, request, state, cancellationToken);
        }
        else
        {
            outcome = await RunExecuteAsync(session, request, state, cancellationToken);
        }

        overall.Stop();

        return ResultEnvelopeBuilder.Build(
            request,
            outcome.Status,
            request.Decision.IsDryRun,
            outcome.ConfirmationToken,
            state,
            overall.ElapsedMilliseconds,
            outcome.Error);
    }

    private async Task<PipelineOutcome> RunReadAsync(
        ITenantExecutionSession session,
        ToolPipelineRequest request,
        PipelineExecutionState state,
        CancellationToken cancellationToken)
    {
        var input = BuildStageInput(request.CanonicalInputJson, snapshotOutput: null);
        var execute = await RunStageAsync(session, request, ToolStage.Execute, input, state, cancellationToken);
        if (!execute.Succeeded)
        {
            return Failure(ToolExecutionStatus.Failed, execute);
        }

        ApplyResultOutput(state, execute);
        return new PipelineOutcome(ToolExecutionStatus.Succeeded, ConfirmationToken: null, Error: null);
    }

    private async Task<PipelineOutcome> RunDryRunAsync(
        ITenantExecutionSession session,
        ToolPipelineRequest request,
        PipelineExecutionState state,
        CancellationToken cancellationToken)
    {
        var preSnapshotInput = BuildStageInput(request.CanonicalInputJson, snapshotOutput: null);
        var snapshot = await RunStageAsync(session, request, ToolStage.Snapshot, preSnapshotInput, state, cancellationToken);
        if (!snapshot.Succeeded)
        {
            return Failure(ToolExecutionStatus.Failed, snapshot);
        }

        if (snapshot.Output.HasValue)
        {
            state.Before = snapshot.Output.Value.Clone();
        }

        var stageInput = BuildStageInput(request.CanonicalInputJson, snapshot.Output);

        var preflight = await RunStageAsync(session, request, ToolStage.Preflight, stageInput, state, cancellationToken);
        state.Preflight = ExtractChecks(preflight.Output);
        if (!preflight.Succeeded)
        {
            return Failure(ToolExecutionStatus.PreflightFailed, preflight);
        }

        var dryRun = await RunStageAsync(session, request, ToolStage.DryRun, stageInput, state, cancellationToken);
        if (!dryRun.Succeeded)
        {
            return Failure(ToolExecutionStatus.Failed, dryRun);
        }

        ApplyResultOutput(state, dryRun);
        return new PipelineOutcome(
            ToolExecutionStatus.DryRunCompleted,
            request.Decision.ConfirmationToken,
            Error: null);
    }

    private async Task<PipelineOutcome> RunExecuteAsync(
        ITenantExecutionSession session,
        ToolPipelineRequest request,
        PipelineExecutionState state,
        CancellationToken cancellationToken)
    {
        var preSnapshotInput = BuildStageInput(request.CanonicalInputJson, snapshotOutput: null);
        var snapshot = await RunStageAsync(session, request, ToolStage.Snapshot, preSnapshotInput, state, cancellationToken);
        if (!snapshot.Succeeded)
        {
            return Failure(ToolExecutionStatus.Failed, snapshot);
        }

        if (snapshot.Output.HasValue)
        {
            state.Before = snapshot.Output.Value.Clone();
        }

        // Snapshot is threaded into every mutating/undo stage so the tool script
        // stays stateless and rollback has the fresh pre-execution state.
        var stageInput = BuildStageInput(request.CanonicalInputJson, snapshot.Output);

        var execute = await RunStageAsync(session, request, ToolStage.Execute, stageInput, state, cancellationToken);
        if (!execute.Succeeded)
        {
            return await HandleFailureAsync(session, request, state, stageInput, execute, verifyFailed: false, cancellationToken);
        }

        ApplyResultOutput(state, execute);

        var verify = await RunStageAsync(session, request, ToolStage.Verify, stageInput, state, cancellationToken);
        state.Verification = ExtractChecks(verify.Output);
        if (!verify.Succeeded)
        {
            return await HandleFailureAsync(session, request, state, stageInput, verify, verifyFailed: true, cancellationToken);
        }

        return new PipelineOutcome(ToolExecutionStatus.Succeeded, ConfirmationToken: null, Error: null);
    }

    private async Task<PipelineOutcome> HandleFailureAsync(
        ITenantExecutionSession session,
        ToolPipelineRequest request,
        PipelineExecutionState state,
        string stageInput,
        StageExecutionResult failure,
        bool verifyFailed,
        CancellationToken cancellationToken)
    {
        var originalError = ResultEnvelopeBuilder.Sanitize(failure.ErrorCode, failure.SanitizedMessage);

        if (request.Manifest.RiskTier < RollbackMinimumRiskTier)
        {
            return new PipelineOutcome(ToolExecutionStatus.Failed, ConfirmationToken: null, originalError);
        }

        var rollback = await RunStageAsync(session, request, ToolStage.Rollback, stageInput, state, cancellationToken);
        if (!rollback.Succeeded)
        {
            var rollbackError = new ToolError(
                StageErrorCodes.RollbackFailed,
                "The change failed and automatic rollback also failed; manual intervention is required.");
            return new PipelineOutcome(ToolExecutionStatus.Failed, ConfirmationToken: null, rollbackError);
        }

        var status = verifyFailed
            ? ToolExecutionStatus.VerifyFailedRolledBack
            : ToolExecutionStatus.RolledBack;
        return new PipelineOutcome(status, ConfirmationToken: null, originalError);
    }

    private async Task<StageExecutionResult> RunStageAsync(
        ITenantExecutionSession session,
        ToolPipelineRequest request,
        ToolStage stage,
        string inputJson,
        PipelineExecutionState state,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var timeout = request.Manifest.TimeoutSeconds > 0
            ? TimeSpan.FromSeconds(request.Manifest.TimeoutSeconds)
            : Timeout.InfiniteTimeSpan;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        var stageRequest = new StageExecutionRequest(session, request.Manifest, stage, inputJson, request.CorrelationId);

        try
        {
            return await _stageExecutor.ExecuteAsync(stageRequest, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Tool stage {Stage} for {ToolId} exceeded its {TimeoutSeconds}s time limit.",
                stage,
                request.Manifest.Id,
                request.Manifest.TimeoutSeconds);
            return StageExecutionResult.Failure(StageErrorCodes.TimeoutExceeded, $"Stage '{stage}' exceeded its time limit.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TenantSessionException and not TenantSessionFatalException)
        {
            _logger.LogError(
                ex,
                "Unexpected error in tool stage {Stage} for {ToolId}.",
                stage,
                request.Manifest.Id);
            return StageExecutionResult.Failure(StageErrorCodes.ExecutionFailed, $"Stage '{stage}' encountered an unexpected error.");
        }
        finally
        {
            stopwatch.Stop();
            state.Timings[stage.ToString()] = stopwatch.ElapsedMilliseconds;
        }
    }

    private ToolResultEnvelope FailedEnvelope(ToolPipelineRequest request, string errorCode, string message) =>
        ResultEnvelopeBuilder.Build(
            request,
            ToolExecutionStatus.Failed,
            dryRun: request.Decision.IsDryRun,
            confirmationToken: null,
            new PipelineExecutionState(),
            totalMs: 0,
            ResultEnvelopeBuilder.Sanitize(errorCode, message));

    private static PipelineOutcome Failure(ToolExecutionStatus status, StageExecutionResult result) =>
        new(status, ConfirmationToken: null, ResultEnvelopeBuilder.Sanitize(result.ErrorCode, result.SanitizedMessage));

    private static void ApplyResultOutput(PipelineExecutionState state, StageExecutionResult result)
    {
        if (result.Output is not { ValueKind: JsonValueKind.Object } output)
        {
            return;
        }

        if (output.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String)
        {
            state.Summary = summary.GetString();
        }

        if (output.TryGetProperty("after", out var after))
        {
            state.After = after.Clone();
        }
    }

    private static IReadOnlyList<ToolCheckResult>? ExtractChecks(JsonElement? output)
    {
        if (output is not { ValueKind: JsonValueKind.Object } obj ||
            !obj.TryGetProperty("checks", out var checks) ||
            checks.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var results = new List<ToolCheckResult>();
        foreach (var item in checks.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var check = item.TryGetProperty("check", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()!
                : "check";
            var passed = item.TryGetProperty("passed", out var p) &&
                p.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                p.GetBoolean();
            var detail = item.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
            results.Add(new ToolCheckResult(check, passed, detail));
        }

        return results.Count == 0 ? null : results;
    }

    private static string BuildStageInput(string canonicalInputJson, JsonElement? snapshotOutput)
    {
        using var inputDocument = JsonDocument.Parse(canonicalInputJson);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("input");
            inputDocument.RootElement.WriteTo(writer);
            writer.WritePropertyName("snapshot");
            if (snapshotOutput.HasValue)
            {
                snapshotOutput.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private readonly record struct PipelineOutcome(
        ToolExecutionStatus Status,
        string? ConfirmationToken,
        ToolError? Error);
}
