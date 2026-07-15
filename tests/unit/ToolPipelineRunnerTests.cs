using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;
using TeamsPhoneMcp.Core.Sessions;

namespace TeamsPhoneMcp.UnitTests;

public class ToolPipelineRunnerTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000009");

    [Fact]
    public async Task ReadTool_RunsExecuteOnly_ReturnsSucceeded()
    {
        var executor = new FakeStageExecutor();
        var manager = new StubSessionManager();
        var runner = Runner(executor, manager);

        var envelope = await runner.ExecuteAsync(
            Request(ReadManifest(), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Succeeded, envelope.Status);
        Assert.Equal([ToolStage.Execute], executor.InvokedStages);
        Assert.Equal(TenantOperationKind.Read, manager.LastOperationKind);
        Assert.False(envelope.DryRun);
        Assert.Null(envelope.Error);
        Assert.True(envelope.Timings!.Stages.ContainsKey("Execute"));
    }

    [Fact]
    public async Task ReadTool_ExecuteFailure_ReturnsFailed()
    {
        var executor = new FakeStageExecutor((_, _) => Task.FromResult(
            StageExecutionResult.Failure(StageErrorCodes.ExecutionFailed, "read failed")));
        var runner = Runner(executor, new StubSessionManager());

        var envelope = await runner.ExecuteAsync(
            Request(ReadManifest(), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, envelope.Status);
        Assert.Equal(StageErrorCodes.ExecutionFailed, envelope.Error!.Code);
        Assert.Equal("read failed", envelope.Error!.Message);
    }

    [Fact]
    public async Task WriteDryRun_RunsSnapshotPreflightDryRun_ReturnsDryRunCompletedWithToken()
    {
        var executor = new FakeStageExecutor();
        var manager = new StubSessionManager();
        var runner = Runner(executor, manager);

        var envelope = await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 1), PolicyDecision.DryRun("token-abc", simulated: false)),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.DryRunCompleted, envelope.Status);
        Assert.Equal("token-abc", envelope.ConfirmationToken);
        Assert.True(envelope.DryRun);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Preflight, ToolStage.DryRun], executor.InvokedStages);
        Assert.Equal(TenantOperationKind.Read, manager.LastOperationKind);
    }

    [Fact]
    public async Task WriteDryRun_PreflightFails_ReturnsPreflightFailed_NoToken()
    {
        var executor = new FakeStageExecutor((request, _) => Task.FromResult(request.Stage switch
        {
            ToolStage.Preflight => StageExecutionResult.Failure(StageErrorCodes.PreflightFailed, "user has no number"),
            _ => StageExecutionResult.Success(null)
        }));
        var runner = Runner(executor, new StubSessionManager());

        var envelope = await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 1), PolicyDecision.DryRun("token-abc", simulated: false)),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.PreflightFailed, envelope.Status);
        Assert.Null(envelope.ConfirmationToken);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Preflight], executor.InvokedStages);
    }

    [Fact]
    public async Task WriteDryRun_PopulatesPreflightChecks()
    {
        var executor = new FakeStageExecutor((request, _) => Task.FromResult(request.Stage switch
        {
            ToolStage.Preflight => StageExecutionResult.Success(JsonSerializer.SerializeToElement(new
            {
                checks = new[] { new { check = "hasNumber", passed = true, detail = "ok" } }
            })),
            _ => StageExecutionResult.Success(null)
        }));
        var runner = Runner(executor, new StubSessionManager());

        var envelope = await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 1), PolicyDecision.DryRun("t", simulated: false)),
            CancellationToken.None);

        Assert.NotNull(envelope.Preflight);
        var check = Assert.Single(envelope.Preflight!);
        Assert.Equal("hasNumber", check.Check);
        Assert.True(check.Passed);
        Assert.Equal("ok", check.Detail);
    }

    [Fact]
    public async Task WriteExecute_RunsSnapshotExecuteVerify_ReturnsSucceeded()
    {
        var executor = new FakeStageExecutor();
        var manager = new StubSessionManager();
        var runner = Runner(executor, manager);

        var envelope = await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 1), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Succeeded, envelope.Status);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Execute, ToolStage.Verify], executor.InvokedStages);
        Assert.Equal(TenantOperationKind.Write, manager.LastOperationKind);
    }

    [Fact]
    public async Task WriteExecute_ExecuteFails_Tier1_DoesNotRollBack()
    {
        var executor = new FakeStageExecutor((request, _) => Task.FromResult(request.Stage switch
        {
            ToolStage.Execute => StageExecutionResult.Failure(StageErrorCodes.ExecutionFailed, "execute failed"),
            _ => StageExecutionResult.Success(null)
        }));
        var runner = Runner(executor, new StubSessionManager());

        var envelope = await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 1), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, envelope.Status);
        Assert.Equal(StageErrorCodes.ExecutionFailed, envelope.Error!.Code);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Execute], executor.InvokedStages);
    }

    [Fact]
    public async Task WriteExecute_ExecuteFails_Tier2_RollsBack()
    {
        var executor = new FakeStageExecutor((request, _) => Task.FromResult(request.Stage switch
        {
            ToolStage.Execute => StageExecutionResult.Failure(StageErrorCodes.ExecutionFailed, "execute failed"),
            _ => StageExecutionResult.Success(null)
        }));
        var runner = Runner(executor, new StubSessionManager());

        var envelope = await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 2), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.RolledBack, envelope.Status);
        Assert.Equal(StageErrorCodes.ExecutionFailed, envelope.Error!.Code);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Execute, ToolStage.Rollback], executor.InvokedStages);
    }

    [Fact]
    public async Task WriteExecute_VerifyFails_Tier2_RollsBack()
    {
        var executor = new FakeStageExecutor((request, _) => Task.FromResult(request.Stage switch
        {
            ToolStage.Verify => StageExecutionResult.Failure(StageErrorCodes.VerifyFailed, "verification failed"),
            _ => StageExecutionResult.Success(null)
        }));
        var runner = Runner(executor, new StubSessionManager());

        var envelope = await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 2), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.VerifyFailedRolledBack, envelope.Status);
        Assert.Equal(StageErrorCodes.VerifyFailed, envelope.Error!.Code);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Execute, ToolStage.Verify, ToolStage.Rollback], executor.InvokedStages);
    }

    [Fact]
    public async Task WriteExecute_RollbackAlsoFails_ReturnsFailedWithRollbackFailed()
    {
        var executor = new FakeStageExecutor((request, _) => Task.FromResult(request.Stage switch
        {
            ToolStage.Execute => StageExecutionResult.Failure(StageErrorCodes.ExecutionFailed, "execute failed"),
            ToolStage.Rollback => StageExecutionResult.Failure(StageErrorCodes.RollbackFailed, "rollback failed"),
            _ => StageExecutionResult.Success(null)
        }));
        var runner = Runner(executor, new StubSessionManager());

        var envelope = await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 2), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, envelope.Status);
        Assert.Equal(StageErrorCodes.RollbackFailed, envelope.Error!.Code);
    }

    [Fact]
    public async Task WriteExecute_ThreadsSnapshotIntoRollbackInput()
    {
        string? rollbackInput = null;
        var executor = new FakeStageExecutor((request, _) =>
        {
            switch (request.Stage)
            {
                case ToolStage.Snapshot:
                    return Task.FromResult(StageExecutionResult.Success(
                        JsonSerializer.SerializeToElement(new { callerId = "A" })));
                case ToolStage.Execute:
                    return Task.FromResult(StageExecutionResult.Failure(StageErrorCodes.ExecutionFailed, "boom"));
                case ToolStage.Rollback:
                    rollbackInput = request.InputJson;
                    return Task.FromResult(StageExecutionResult.Success(null));
                default:
                    return Task.FromResult(StageExecutionResult.Success(null));
            }
        });
        var runner = Runner(executor, new StubSessionManager());

        await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 2), PolicyDecision.Execute(), "{\"userUpn\":\"a@x.com\"}"),
            CancellationToken.None);

        Assert.NotNull(rollbackInput);
        using var document = JsonDocument.Parse(rollbackInput!);
        Assert.Equal("a@x.com", document.RootElement.GetProperty("input").GetProperty("userUpn").GetString());
        Assert.Equal("A", document.RootElement.GetProperty("snapshot").GetProperty("callerId").GetString());
    }

    [Fact]
    public async Task WriteExecute_PopulatesDiffFromSnapshotAndAfter()
    {
        var executor = new FakeStageExecutor((request, _) => Task.FromResult(request.Stage switch
        {
            ToolStage.Snapshot => StageExecutionResult.Success(JsonSerializer.SerializeToElement(new { callerId = "A" })),
            ToolStage.Execute => StageExecutionResult.Success(JsonSerializer.SerializeToElement(new
            {
                summary = "Moved caller ID to B.",
                after = new { callerId = "B" }
            })),
            _ => StageExecutionResult.Success(null)
        }));
        var runner = Runner(executor, new StubSessionManager());

        var envelope = await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 1), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Succeeded, envelope.Status);
        Assert.Equal("Moved caller ID to B.", envelope.Summary);
        Assert.Equal("A", envelope.Diff!.Before!.Value.GetProperty("callerId").GetString());
        Assert.Equal("B", envelope.Diff!.After!.Value.GetProperty("callerId").GetString());
    }

    [Fact]
    public async Task Stage_ExceedingTimeout_ReturnsTimeoutExceeded()
    {
        var executor = new FakeStageExecutor(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return StageExecutionResult.Success(null);
        });
        var runner = Runner(executor, new StubSessionManager());

        var envelope = await runner.ExecuteAsync(
            Request(ReadManifest(timeoutSeconds: 1), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, envelope.Status);
        Assert.Equal(StageErrorCodes.TimeoutExceeded, envelope.Error!.Code);
    }

    [Fact]
    public async Task ExternalCancellation_Propagates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new FakeStageExecutor(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return StageExecutionResult.Success(null);
        });
        var runner = Runner(executor, new StubSessionManager());
        using var cts = new CancellationTokenSource();

        var task = runner.ExecuteAsync(
            Request(ReadManifest(), PolicyDecision.Execute()),
            cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task SessionUnavailable_ReturnsFailedWithoutLeakingDetails()
    {
        var manager = new StubSessionManager(
            new TenantSessionException("tenantSessionFactoryUnavailable", "internal detail"));
        var runner = Runner(new FakeStageExecutor(), manager);

        var envelope = await runner.ExecuteAsync(
            Request(ReadManifest(), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, envelope.Status);
        Assert.Equal("tenantSessionFactoryUnavailable", envelope.Error!.Code);
        Assert.Equal("The tenant session is unavailable.", envelope.Error!.Message);
    }

    [Fact]
    public async Task FatalSessionError_ReturnsFailed()
    {
        var executor = new FakeStageExecutor((_, _) =>
            throw new TenantSessionFatalException("runspaceCrashed", "internal detail"));
        var runner = Runner(executor, new StubSessionManager());

        var envelope = await runner.ExecuteAsync(
            Request(ReadManifest(), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, envelope.Status);
        Assert.Equal("runspaceCrashed", envelope.Error!.Code);
        Assert.Equal("The tenant session failed and was reset.", envelope.Error!.Message);
    }

    [Fact]
    public async Task RejectedDecision_ReturnsFailed_WithoutAcquiringSession()
    {
        var manager = new StubSessionManager();
        var runner = Runner(new FakeStageExecutor(), manager);

        var envelope = await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 1), PolicyDecision.Reject("tierGated", "not allowed")),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, envelope.Status);
        Assert.Equal("tierGated", envelope.Error!.Code);
        Assert.Equal(0, manager.CallCount);
    }

    [Fact]
    public async Task Integration_WithRealSessionManager_ReadAndWriteComplete()
    {
        await using var manager = new TenantSessionManager(
            new InlineSessionFactory(),
            Options.Create(new TenantSessionOptions()),
            TimeProvider.System,
            NullLogger<TenantSessionManager>.Instance);
        var runner = Runner(new FakeStageExecutor(), manager);

        var read = await runner.ExecuteAsync(
            Request(ReadManifest(), PolicyDecision.Execute()),
            CancellationToken.None);
        var write = await runner.ExecuteAsync(
            Request(WriteManifest(riskTier: 1), PolicyDecision.Execute()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Succeeded, read.Status);
        Assert.Equal(ToolExecutionStatus.Succeeded, write.Status);
    }

    private static ToolPipelineRunner Runner(IStageExecutor executor, ITenantSessionManager manager) =>
        new(manager, executor, NullLogger<ToolPipelineRunner>.Instance);

    private static ToolPipelineRequest Request(
        ToolManifest manifest,
        PolicyDecision decision,
        string inputJson = "{\"userUpn\":\"a@x.com\"}") =>
        new(manifest, inputJson, new TenantSessionContext(TenantId, "cred"), decision, "corr-1");

    private static ToolManifest ReadManifest(int timeoutSeconds = 300) =>
        Manifest("get-thing", riskTier: 0, timeoutSeconds);

    private static ToolManifest WriteManifest(int riskTier, int timeoutSeconds = 300) =>
        Manifest("write-thing", riskTier, timeoutSeconds);

    private static ToolManifest Manifest(string id, int riskTier, int timeoutSeconds) => new()
    {
        Id = id,
        Version = "1.0.0",
        Summary = "Sample tool.",
        Category = riskTier == 0 ? "read" : "change",
        RiskTier = riskTier,
        Annotations = new ToolManifestAnnotations
        {
            ReadOnlyHint = riskTier == 0,
            DestructiveHint = false,
            IdempotentHint = true
        },
        Inputs = new Dictionary<string, ToolManifestInput>(),
        MaxBlastRadius = 1,
        TimeoutSeconds = timeoutSeconds
    };

    private sealed class StubSessionManager(Exception? throwBeforeCallback = null) : ITenantSessionManager
    {
        public TenantOperationKind? LastOperationKind { get; private set; }

        public int CallCount { get; private set; }

        public Task<TResult> ExecuteAsync<TResult>(
            TenantSessionContext context,
            TenantOperationKind operationKind,
            Func<ITenantExecutionSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
        {
            LastOperationKind = operationKind;
            if (throwBeforeCallback is not null)
            {
                return Task.FromException<TResult>(throwBeforeCallback);
            }

            CallCount++;
            return operation(new FakeSession(context), cancellationToken);
        }
    }

    private sealed class InlineSessionFactory : ITenantSessionFactory
    {
        public ValueTask<ITenantExecutionSession> CreateAsync(
            TenantSessionContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ITenantExecutionSession>(new FakeSession(context));
    }

    private sealed class FakeSession(TenantSessionContext context) : ITenantExecutionSession
    {
        public TenantSessionContext Context { get; } = context;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
