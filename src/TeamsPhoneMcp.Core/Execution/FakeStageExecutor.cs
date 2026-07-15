using System.Text.Json;

namespace TeamsPhoneMcp.Core.Execution;

/// <summary>
/// Deterministic, offline <see cref="IStageExecutor"/> for tests and local
/// development. It performs no PowerShell, credential, or tenant I/O. Supply a
/// custom handler to script per-stage success, failure, or timeout behavior.
/// </summary>
public sealed class FakeStageExecutor : IStageExecutor
{
    private readonly Func<StageExecutionRequest, CancellationToken, Task<StageExecutionResult>> _handler;
    private readonly List<ToolStage> _invokedStages = [];
    private readonly object _sync = new();

    public FakeStageExecutor()
        : this((request, _) => Task.FromResult(StageExecutionResult.Success(EchoOutput(request.Stage))))
    {
    }

    public FakeStageExecutor(Func<StageExecutionRequest, CancellationToken, Task<StageExecutionResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <summary>Stages invoked so far, in order, across all calls.</summary>
    public IReadOnlyList<ToolStage> InvokedStages
    {
        get
        {
            lock (_sync)
            {
                return _invokedStages.ToArray();
            }
        }
    }

    public async Task<StageExecutionResult> ExecuteAsync(StageExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_sync)
        {
            _invokedStages.Add(request.Stage);
        }

        return await _handler(request, cancellationToken);
    }

    /// <summary>Produces a minimal, detached success output for a stage.</summary>
    public static JsonElement EchoOutput(ToolStage stage) =>
        JsonSerializer.SerializeToElement(new
        {
            stage = stage.ToString(),
            summary = $"{stage} completed."
        });
}
