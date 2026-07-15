namespace TeamsPhoneMcp.Core.Execution;

/// <summary>
/// Stable, sanitized error codes surfaced through <see cref="ToolResultEnvelope"/>.
/// Codes are safe to return to clients; they never contain tenant identifiers,
/// credential references, or raw exception text.
/// </summary>
public static class StageErrorCodes
{
    public const string ExecutionFailed = "executionFailed";
    public const string PreflightFailed = "preflightFailed";
    public const string VerifyFailed = "verifyFailed";
    public const string RollbackFailed = "rollbackFailed";
    public const string TimeoutExceeded = "timeoutExceeded";
    public const string OperationCancelled = "operationCancelled";
    public const string AuthenticationFailed = "authenticationFailed";
    public const string AuthorizationFailed = "authorizationFailed";
    public const string Throttled = "throttled";
    public const string MalformedStageOutput = "malformedStageOutput";
    public const string SessionUnavailable = "sessionUnavailable";
}
