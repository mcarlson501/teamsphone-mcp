using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Sessions;

namespace TeamsPhoneMcp.UnitTests;

/// <summary>
/// Exercises the in-process PowerShell <see cref="RunspaceStageExecutor"/> fully
/// offline: real runspaces (no MicrosoftTeams module, no tenant) plus fixture
/// <c>run.ps1</c> scripts under <c>Fixtures/tools</c>.
/// </summary>
public sealed class RunspaceStageExecutorTests
{
    private static readonly string FixturesRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "tools");

    private static readonly string HostInput =
        "{\"input\":{\"foo\":\"bar\"},\"snapshot\":null}";

    [Fact]
    public async Task ExecuteAsync_SuccessfulStage_ReturnsParsedOutput()
    {
        var result = await RunAsync("echo-tool", ToolStage.Execute, HostInput);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.NotNull(result.Output);
        Assert.Equal("echo:execute", result.Output!.Value.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_ThreadsInputEnvelopeToScript()
    {
        var result = await RunAsync("echo-tool", ToolStage.DryRun, HostInput);

        Assert.True(result.Succeeded);
        var after = result.Output!.Value.GetProperty("after");
        Assert.Equal("bar", after.GetProperty("echoedInput").GetProperty("foo").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("snapshot").ValueKind);
    }

    [Theory]
    [InlineData(ToolStage.Snapshot, "snapshot")]
    [InlineData(ToolStage.Preflight, "preflight")]
    [InlineData(ToolStage.DryRun, "dryrun")]
    [InlineData(ToolStage.Execute, "execute")]
    [InlineData(ToolStage.Verify, "verify")]
    [InlineData(ToolStage.Rollback, "rollback")]
    public async Task ExecuteAsync_MapsEveryStageToScriptToken(ToolStage stage, string expectedToken)
    {
        var result = await RunAsync("echo-tool", stage, HostInput);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedToken, result.Output!.Value.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_ErrorStream_ReturnsSanitizedFailure()
    {
        var result = await RunAsync("stderr-tool", ToolStage.Execute, HostInput);

        Assert.False(result.Succeeded);
        Assert.Equal(StageErrorCodes.ExecutionFailed, result.ErrorCode);
        Assert.DoesNotContain("555-0100", result.SanitizedMessage);
    }

    [Fact]
    public async Task ExecuteAsync_TerminatingThrow_ReturnsSanitizedFailure()
    {
        var result = await RunAsync("throw-tool", ToolStage.Execute, HostInput);

        Assert.False(result.Succeeded);
        Assert.Equal(StageErrorCodes.ExecutionFailed, result.ErrorCode);
        Assert.DoesNotContain("555-0199", result.SanitizedMessage);
    }

    [Fact]
    public async Task ExecuteAsync_NoOutput_ReturnsMalformed()
    {
        var result = await RunAsync("empty-output-tool", ToolStage.Execute, HostInput);

        Assert.False(result.Succeeded);
        Assert.Equal(StageErrorCodes.MalformedStageOutput, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleOutputs_ReturnsMalformed()
    {
        var result = await RunAsync("multi-output-tool", ToolStage.Execute, HostInput);

        Assert.False(result.Succeeded);
        Assert.Equal(StageErrorCodes.MalformedStageOutput, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_NonStringOutput_ReturnsMalformed()
    {
        var result = await RunAsync("non-string-tool", ToolStage.Execute, HostInput);

        Assert.False(result.Succeeded);
        Assert.Equal(StageErrorCodes.MalformedStageOutput, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_NonJsonStringOutput_ReturnsMalformed()
    {
        var result = await RunAsync("malformed-json-tool", ToolStage.Execute, HostInput);

        Assert.False(result.Succeeded);
        Assert.Equal(StageErrorCodes.MalformedStageOutput, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_MissingScript_ReturnsMalformed()
    {
        var result = await RunAsync("does-not-exist-tool", ToolStage.Execute, HostInput);

        Assert.False(result.Succeeded);
        Assert.Equal(StageErrorCodes.MalformedStageOutput, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_NonPowerShellSession_ReturnsSessionUnavailable()
    {
        var executor = new RunspaceStageExecutor(new ToolScriptLocator(FixturesRoot), NullLogger<RunspaceStageExecutor>.Instance);
        var request = new StageExecutionRequest(
            new NonPowerShellSession(Context()),
            Manifest("echo-tool"),
            ToolStage.Execute,
            HostInput,
            Guid.NewGuid().ToString());

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(StageErrorCodes.SessionUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_ThrowsOperationCanceled()
    {
        var executor = new RunspaceStageExecutor(new ToolScriptLocator(FixturesRoot), NullLogger<RunspaceStageExecutor>.Instance);
        await using var session = new LocalRunspaceSession(Context());
        var request = new StageExecutionRequest(
            session,
            Manifest("slow-tool"),
            ToolStage.Execute,
            HostInput,
            Guid.NewGuid().ToString());

        using var cts = new CancellationTokenSource();
        var task = executor.ExecuteAsync(request, cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Pipeline_ReadTool_RunsRealRunspaceOfflineAndSucceeds()
    {
        var executor = new RunspaceStageExecutor(new ToolScriptLocator(FixturesRoot), NullLogger<RunspaceStageExecutor>.Instance);
        var runner = new ToolPipelineRunner(
            new LocalRunspaceSessionManager(),
            executor,
            NullLogger<ToolPipelineRunner>.Instance);

        var request = new ToolPipelineRequest(
            Manifest("echo-tool", riskTier: 0),
            "{\"foo\":\"bar\"}",
            Context(),
            TeamsPhoneMcp.Core.Policy.PolicyDecision.Execute(),
            Guid.NewGuid().ToString());

        var envelope = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Succeeded, envelope.Status);
        Assert.Equal("echo:execute", envelope.Summary);
    }

    private static async Task<StageExecutionResult> RunAsync(string toolId, ToolStage stage, string inputJson)
    {
        var executor = new RunspaceStageExecutor(new ToolScriptLocator(FixturesRoot), NullLogger<RunspaceStageExecutor>.Instance);
        await using var session = new LocalRunspaceSession(Context());
        var request = new StageExecutionRequest(session, Manifest(toolId), stage, inputJson, Guid.NewGuid().ToString());
        return await executor.ExecuteAsync(request, CancellationToken.None);
    }

    private static TenantSessionContext Context() =>
        new(Guid.NewGuid(), "cred-ref");

    private static ToolManifest Manifest(string id, int riskTier = 0, int timeoutSeconds = 60) => new()
    {
        Id = id,
        Version = "1.0.0",
        Summary = "fixture tool",
        Category = riskTier == 0 ? "read" : "change",
        RiskTier = riskTier,
        Annotations = new ToolManifestAnnotations(),
        Inputs = new Dictionary<string, ToolManifestInput>(),
        TimeoutSeconds = timeoutSeconds,
    };

    private sealed class LocalRunspaceSessionManager : ITenantSessionManager
    {
        public async Task<TResult> ExecuteAsync<TResult>(
            TenantSessionContext context,
            TenantOperationKind operationKind,
            Func<ITenantExecutionSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
        {
            await using var session = new LocalRunspaceSession(context);
            return await operation(session, cancellationToken);
        }
    }

    private sealed class NonPowerShellSession : ITenantExecutionSession
    {
        public NonPowerShellSession(TenantSessionContext context) => Context = context;

        public TenantSessionContext Context { get; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
