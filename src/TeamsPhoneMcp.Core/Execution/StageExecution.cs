using System.Text.Json;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Sessions;

namespace TeamsPhoneMcp.Core.Execution;

/// <summary>
/// A single stage invocation against a live tenant session. The
/// <see cref="InputJson"/> is a host-owned envelope so the tool script stays
/// stateless: for stages after <see cref="ToolStage.Snapshot"/> it carries the
/// original input plus the captured snapshot (see <c>ToolPipelineRunner</c>).
/// </summary>
public sealed record StageExecutionRequest(
    ITenantExecutionSession Session,
    ToolManifest Manifest,
    ToolStage Stage,
    string InputJson,
    string CorrelationId);

/// <summary>
/// Result of a single stage invocation. Executors are responsible for producing
/// an already-sanitized <see cref="SanitizedMessage"/> and a stable
/// <see cref="ErrorCode"/> from <see cref="StageErrorCodes"/>.
/// </summary>
public sealed record StageExecutionResult
{
    private StageExecutionResult(bool succeeded, JsonElement? output, string? errorCode, string? sanitizedMessage)
    {
        Succeeded = succeeded;
        Output = output;
        ErrorCode = errorCode;
        SanitizedMessage = sanitizedMessage;
    }

    public bool Succeeded { get; }

    /// <summary>Detached (cloned) stage output; safe to retain past the executor call.</summary>
    public JsonElement? Output { get; }

    public string? ErrorCode { get; }

    public string? SanitizedMessage { get; }

    public static StageExecutionResult Success(JsonElement? output) =>
        new(true, output, null, null);

    public static StageExecutionResult Failure(string errorCode, string sanitizedMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedMessage);
        return new StageExecutionResult(false, null, errorCode, sanitizedMessage);
    }
}

/// <summary>
/// Invokes a single tool stage inside an already-established tenant session.
/// The offline <see cref="FakeStageExecutor"/> implements this for tests; the
/// in-process PowerShell executor implements it in a later milestone.
/// </summary>
public interface IStageExecutor
{
    Task<StageExecutionResult> ExecuteAsync(StageExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Fail-closed default executor. Mirrors <c>UnconfiguredTenantSessionFactory</c>:
/// until a real in-process PowerShell executor is registered, stage execution is
/// unavailable. In production this is additionally unreachable because the default
/// tenant session factory already fails closed before any stage runs.
/// </summary>
internal sealed class UnconfiguredStageExecutor : IStageExecutor
{
    public Task<StageExecutionResult> ExecuteAsync(StageExecutionRequest request, CancellationToken cancellationToken) =>
        throw new TenantSessionException(
            StageErrorCodes.SessionUnavailable,
            "Tool stage execution is not configured.");
}
