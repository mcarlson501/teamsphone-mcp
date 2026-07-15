using System.Text.Json;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Sessions;

namespace TeamsPhoneMcp.UnitTests;

public class FakeStageExecutorTests
{
    [Fact]
    public async Task DefaultExecutor_SucceedsAndEchoesStage()
    {
        var executor = new FakeStageExecutor();

        var result = await executor.ExecuteAsync(Request(ToolStage.Execute), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Output);
        Assert.Equal("Execute", result.Output!.Value.GetProperty("stage").GetString());
        Assert.Equal([ToolStage.Execute], executor.InvokedStages);
    }

    [Fact]
    public async Task CustomHandler_ControlsPerStageOutcome()
    {
        var executor = new FakeStageExecutor((request, _) => Task.FromResult(
            request.Stage == ToolStage.Preflight
                ? StageExecutionResult.Failure(StageErrorCodes.PreflightFailed, "blocked")
                : StageExecutionResult.Success(null)));

        var snapshot = await executor.ExecuteAsync(Request(ToolStage.Snapshot), CancellationToken.None);
        var preflight = await executor.ExecuteAsync(Request(ToolStage.Preflight), CancellationToken.None);

        Assert.True(snapshot.Succeeded);
        Assert.False(preflight.Succeeded);
        Assert.Equal(StageErrorCodes.PreflightFailed, preflight.ErrorCode);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Preflight], executor.InvokedStages);
    }

    private static StageExecutionRequest Request(ToolStage stage) => new(
        new StubSession(),
        new ToolManifest
        {
            Id = "sample",
            Version = "1.0.0",
            Summary = "s",
            Category = "read",
            RiskTier = 0,
            Annotations = new ToolManifestAnnotations(),
            Inputs = new Dictionary<string, ToolManifestInput>()
        },
        stage,
        "{}",
        "corr");

    private sealed class StubSession : ITenantExecutionSession
    {
        public TenantSessionContext Context { get; } = new(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "cred");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
