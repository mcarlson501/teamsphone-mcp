using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeamsPhoneMcp.Core.Sessions;

namespace TeamsPhoneMcp.Core.Execution;

/// <summary>
/// In-process PowerShell stage executor (build spec §6.2). Invokes a tool's
/// <c>run.ps1</c> inside the tenant session's already-connected runspace with
/// <c>-Stage</c> and <c>-InputJson</c>, and maps the result to the stage
/// contract: exactly one JSON string on the output stream is the stage result;
/// any error-stream content, terminating error, or malformed output is a
/// failure. Sanitized failure messages never carry raw cmdlet output or tenant
/// data — those go only to the server logs, keyed by correlation id.
/// </summary>
public sealed class RunspaceStageExecutor : IStageExecutor
{
    private readonly ToolScriptLocator _scriptLocator;
    private readonly ILogger<RunspaceStageExecutor> _logger;

    public RunspaceStageExecutor(ToolScriptLocator scriptLocator, ILogger<RunspaceStageExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(scriptLocator);
        ArgumentNullException.ThrowIfNull(logger);

        _scriptLocator = scriptLocator;
        _logger = logger;
    }

    public async Task<StageExecutionResult> ExecuteAsync(StageExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Session is not IPowerShellTenantSession session)
        {
            return StageExecutionResult.Failure(
                StageErrorCodes.SessionUnavailable,
                "The tenant session does not support PowerShell execution.");
        }

        if (session.Runspace.RunspaceStateInfo.State != RunspaceState.Opened)
        {
            return StageExecutionResult.Failure(
                StageErrorCodes.SessionUnavailable,
                "The tenant session runspace is not open.");
        }

        if (!_scriptLocator.TryResolve(request.Manifest.Id, out var scriptPath))
        {
            _logger.LogError("Tool script for {ToolId} was not found under the tools root.", request.Manifest.Id);
            return StageExecutionResult.Failure(
                StageErrorCodes.MalformedStageOutput,
                "The tool script could not be located.");
        }

        var stageToken = PowerShellStageMapping.ToScriptToken(request.Stage);

        using var powerShell = PowerShell.Create();
        powerShell.Runspace = session.Runspace;
        powerShell
            .AddCommand(scriptPath)
            .AddParameter("Stage", stageToken)
            .AddParameter("InputJson", request.InputJson);

        PSDataCollection<PSObject> output;
        try
        {
            output = await InvokeAsync(powerShell, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Tool {ToolId} stage {Stage} threw a terminating error. Correlation: {CorrelationId}",
                request.Manifest.Id,
                stageToken,
                request.CorrelationId);
            return StageExecutionResult.Failure(StageErrorCodes.ExecutionFailed, $"Stage '{stageToken}' failed.");
        }

        if (powerShell.HadErrors || powerShell.Streams.Error.Count > 0)
        {
            LogStreamErrors(powerShell, request, stageToken);
            return StageExecutionResult.Failure(StageErrorCodes.ExecutionFailed, $"Stage '{stageToken}' failed.");
        }

        return ParseOutput(output, stageToken);
    }

    /// <summary>
    /// Bridges the PowerShell APM begin/end pair to a task. On cancellation the
    /// pipeline is stopped, which surfaces as <see cref="PipelineStoppedException"/>;
    /// that is translated to <see cref="OperationCanceledException"/> so the
    /// pipeline runner can distinguish a timeout/cancel from a stage failure.
    /// </summary>
    private static async Task<PSDataCollection<PSObject>> InvokeAsync(PowerShell powerShell, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var asyncResult = powerShell.BeginInvoke();
        await using (cancellationToken.Register(static state => ((PowerShell)state!).Stop(), powerShell).ConfigureAwait(false))
        {
            try
            {
                return await Task.Factory
                    .FromAsync(asyncResult, powerShell.EndInvoke)
                    .ConfigureAwait(false);
            }
            catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    private static StageExecutionResult ParseOutput(PSDataCollection<PSObject> output, string stageToken)
    {
        if (output.Count != 1)
        {
            return StageExecutionResult.Failure(
                StageErrorCodes.MalformedStageOutput,
                $"Stage '{stageToken}' did not return exactly one result object.");
        }

        if (output[0]?.BaseObject is not string json)
        {
            return StageExecutionResult.Failure(
                StageErrorCodes.MalformedStageOutput,
                $"Stage '{stageToken}' did not return a JSON result string.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return StageExecutionResult.Success(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return StageExecutionResult.Failure(
                StageErrorCodes.MalformedStageOutput,
                $"Stage '{stageToken}' returned malformed JSON.");
        }
    }

    private void LogStreamErrors(PowerShell powerShell, StageExecutionRequest request, string stageToken)
    {
        _logger.LogWarning(
            "Tool {ToolId} stage {Stage} reported {ErrorCount} error(s). Correlation: {CorrelationId}",
            request.Manifest.Id,
            stageToken,
            powerShell.Streams.Error.Count,
            request.CorrelationId);

        foreach (var error in powerShell.Streams.Error)
        {
            _logger.LogDebug(
                "Tool {ToolId} stage {Stage} error detail (correlation {CorrelationId}): {Error}",
                request.Manifest.Id,
                stageToken,
                request.CorrelationId,
                error.ToString());
        }
    }
}
