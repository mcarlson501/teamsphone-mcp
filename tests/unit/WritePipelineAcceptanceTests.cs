using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsPhoneMcp.Audit;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;
using TeamsPhoneMcp.Core.Sessions;

namespace TeamsPhoneMcp.UnitTests;

/// <summary>
/// M4 acceptance: the full write pipeline proven end to end on the shipped
/// <c>move-number-between-users</c> manifest — dry-run → confirmation token →
/// execute → verify, and a forced verification failure that rolls back and
/// lands both snapshots in the audit trail (build spec §6.2–6.4, §9).
/// </summary>
public sealed class WritePipelineAcceptanceTests : IDisposable
{
    private const string ToolId = "move-number-between-users";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";
    private const string SourceUpn = "alice@contoso.com";
    private const string TargetUpn = "bob@contoso.com";
    private const string PhoneNumber = "+15551234567";

    private static readonly Guid TenantGuid = Guid.Parse(TenantId);
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly ToolManifest Manifest = LoadShippedManifest();

    private readonly TempAuditRoot _auditRoot = new();

    public void Dispose() => _auditRoot.Dispose();

    [Fact]
    public void ShippedManifest_DeclaresTheFullWriteContract()
    {
        Assert.Equal("move", Manifest.Category);
        Assert.Equal(2, Manifest.RiskTier);
        Assert.Equal(1, Manifest.MaxBlastRadius);
        Assert.False(Manifest.Annotations.ReadOnlyHint);
        Assert.True(Manifest.Annotations.IdempotentHint);
        Assert.NotEmpty(Manifest.Preflight);
        Assert.NotEmpty(Manifest.Verification);
        Assert.False(string.IsNullOrWhiteSpace(Manifest.Rollback));
    }

    [Fact]
    public async Task DryRunThenConfirmedExecute_CompletesAndIsFullyAudited()
    {
        var engine = CreateEngine();
        var parameters = CreateParameters();
        var (recorder, readRecords) = CreateRecorder();

        var dryRunDecision = engine.Evaluate(
            Manifest,
            new WritePolicyRequest(TenantId, parameters, null, null, null, 1, false, 3, false),
            Now);

        Assert.True(dryRunDecision.Approved);
        Assert.True(dryRunDecision.IsDryRun);
        Assert.False(string.IsNullOrWhiteSpace(dryRunDecision.ConfirmationToken));

        var dryRunExecutor = MoveExecutor();
        var dryRunEnvelope = await CreateRunner(dryRunExecutor).ExecuteAsync(
            CreateRequest(dryRunDecision, "corr-dry"),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.DryRunCompleted, dryRunEnvelope.Status);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Preflight, ToolStage.DryRun], dryRunExecutor.InvokedStages);
        Assert.Equal(dryRunDecision.ConfirmationToken, dryRunEnvelope.ConfirmationToken);
        Assert.All(dryRunEnvelope.Preflight!, check => Assert.True(check.Passed));
        await recorder.RecordAsync(CreateContext("corr-dry", parameters), dryRunEnvelope);

        // The token is what promotes the simulation into a real change.
        var executeDecision = engine.Evaluate(
            Manifest,
            new WritePolicyRequest(TenantId, parameters, false, null, dryRunDecision.ConfirmationToken, 1, false, 3, false),
            Now.AddMinutes(1));

        Assert.True(executeDecision.Approved);
        Assert.False(executeDecision.IsDryRun);

        var executor = MoveExecutor();
        var envelope = await CreateRunner(executor).ExecuteAsync(
            CreateRequest(executeDecision, "corr-exec"),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Succeeded, envelope.Status);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Execute, ToolStage.Verify], executor.InvokedStages);
        Assert.Null(envelope.Error);
        Assert.All(envelope.Verification!, check => Assert.True(check.Passed));
        Assert.NotNull(envelope.Diff?.Before);
        Assert.NotNull(envelope.Diff?.After);
        await recorder.RecordAsync(CreateContext("corr-exec", parameters), envelope);

        var records = readRecords();
        Assert.Equal(2, records.Count);
        var dryRunRecord = Assert.Single(records, r => r.GetProperty("correlationId").GetString() == "corr-dry");
        Assert.Equal("DryRunCompleted", dryRunRecord.GetProperty("status").GetString());
        Assert.True(dryRunRecord.GetProperty("dryRun").GetBoolean());

        var executeRecord = Assert.Single(records, r => r.GetProperty("correlationId").GetString() == "corr-exec");
        Assert.Equal("Succeeded", executeRecord.GetProperty("status").GetString());
        Assert.False(executeRecord.GetProperty("dryRun").GetBoolean());
        Assert.Equal(ToolId, executeRecord.GetProperty("toolId").GetString());
        Assert.Equal(2, executeRecord.GetProperty("riskTier").GetInt32());
        AssertBothSnapshotsStored(executeRecord);
    }

    [Fact]
    public async Task ForcedVerificationFailure_RollsBackAndAuditsBothSnapshots()
    {
        var engine = CreateEngine();
        var parameters = CreateParameters();
        var (recorder, readRecords) = CreateRecorder();

        var dryRun = engine.Evaluate(
            Manifest,
            new WritePolicyRequest(TenantId, parameters, null, null, null, 1, false, 3, false),
            Now);
        var decision = engine.Evaluate(
            Manifest,
            new WritePolicyRequest(TenantId, parameters, false, null, dryRun.ConfirmationToken, 1, false, 3, false),
            Now.AddMinutes(1));

        var executor = MoveExecutor(verifyPasses: false);
        var envelope = await CreateRunner(executor).ExecuteAsync(
            CreateRequest(decision, "corr-verify-fail"),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.VerifyFailedRolledBack, envelope.Status);
        Assert.Equal(
            [ToolStage.Snapshot, ToolStage.Execute, ToolStage.Verify, ToolStage.Rollback],
            executor.InvokedStages);
        Assert.Equal(StageErrorCodes.VerifyFailed, envelope.Error!.Code);
        Assert.Contains(envelope.Verification!, check => !check.Passed);

        await recorder.RecordAsync(CreateContext("corr-verify-fail", parameters), envelope);

        var record = Assert.Single(readRecords());
        Assert.Equal("VerifyFailedRolledBack", record.GetProperty("status").GetString());
        Assert.Equal(StageErrorCodes.VerifyFailed, record.GetProperty("errorCode").GetString());
        Assert.Contains(
            record.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("phase").GetString() == "verify" && !check.GetProperty("passed").GetBoolean());
        AssertBothSnapshotsStored(record);
    }

    [Fact]
    public async Task FailedPreflightCheck_BlocksTheDryRunAndIssuesNoToken()
    {
        var engine = CreateEngine();
        var parameters = CreateParameters();

        var decision = engine.Evaluate(
            Manifest,
            new WritePolicyRequest(TenantId, parameters, null, null, null, 1, false, 3, false),
            Now);

        var executor = MoveExecutor(preflightPasses: false);
        var envelope = await CreateRunner(executor).ExecuteAsync(
            CreateRequest(decision, "corr-preflight-fail"),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.PreflightFailed, envelope.Status);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Preflight], executor.InvokedStages);
        Assert.Null(envelope.ConfirmationToken);
        Assert.Equal(StageErrorCodes.PreflightFailed, envelope.Error!.Code);
        Assert.Contains(envelope.Preflight!, check => !check.Passed);
    }

    [Fact]
    public void ChangedParameters_InvalidateTheConfirmationToken()
    {
        var engine = CreateEngine();

        var dryRun = engine.Evaluate(
            Manifest,
            new WritePolicyRequest(TenantId, CreateParameters(), null, null, null, 1, false, 3, false),
            Now);

        var tampered = engine.Evaluate(
            Manifest,
            new WritePolicyRequest(
                TenantId,
                CreateParameters(targetUpn: "mallory@contoso.com"),
                false,
                null,
                dryRun.ConfirmationToken,
                1,
                false,
                3,
                false),
            Now.AddMinutes(1));

        Assert.False(tampered.Approved);
        Assert.Equal("invalidConfirmationToken", tampered.ErrorCode);
    }

    private void AssertBothSnapshotsStored(JsonElement record)
    {
        var refs = record.GetProperty("snapshotRefs");
        var before = refs.GetProperty("before").GetString();
        var after = refs.GetProperty("after").GetString();

        Assert.False(string.IsNullOrWhiteSpace(before));
        Assert.False(string.IsNullOrWhiteSpace(after));
        Assert.True(File.Exists(ResolveSnapshotPath(before!)), $"Missing snapshot file '{before}'.");
        Assert.True(File.Exists(ResolveSnapshotPath(after!)), $"Missing snapshot file '{after}'.");
    }

    private string ResolveSnapshotPath(string snapshotRef) =>
        Path.Combine(_auditRoot.Path, snapshotRef.Replace('/', Path.DirectorySeparatorChar));

    private (ToolAuditRecorder Recorder, Func<IReadOnlyList<JsonElement>> ReadRecords) CreateRecorder()
    {
        var options = new TestOptionsMonitor<AuditOptions>(new AuditOptions { RootPath = _auditRoot.Path });
        var sink = new JsonlAuditSink(_auditRoot.Resolver, options, NullLogger<JsonlAuditSink>.Instance);
        var snapshotStore = new FileAuditSnapshotStore(_auditRoot.Resolver, options, NullLogger<FileAuditSnapshotStore>.Instance);
        var recorder = new ToolAuditRecorder(
            sink,
            snapshotStore,
            new FixedTimeProvider(Now),
            NullLogger<ToolAuditRecorder>.Instance);

        return (recorder, ReadRecords);
    }

    private IReadOnlyList<JsonElement> ReadRecords()
    {
        var records = new List<JsonElement>();
        foreach (var file in Directory.EnumerateFiles(_auditRoot.Path, "*.jsonl", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    records.Add(JsonDocument.Parse(line).RootElement.Clone());
                }
            }
        }

        return records;
    }

    private static ToolManifest LoadShippedManifest()
    {
        var toolsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools"));
        var catalog = new ToolManifestCatalog(toolsRoot, NullLogger<ToolManifestCatalog>.Instance);
        return catalog.GetRequired(ToolId);
    }

    private static WritePolicyEngine CreateEngine() =>
        new(new ConfirmationTokenService(new byte[32], TimeSpan.FromMinutes(15)));

    private static ToolPipelineRunner CreateRunner(IStageExecutor executor) =>
        new(new InlineSessionManager(), executor, NullLogger<ToolPipelineRunner>.Instance);

    private static ToolPipelineRequest CreateRequest(PolicyDecision decision, string correlationId) =>
        new(
            Manifest,
            CreateParameters().GetRawText(),
            new TenantSessionContext(TenantGuid, "contoso-cred"),
            decision,
            correlationId);

    private static ToolAuditContext CreateContext(string correlationId, JsonElement parameters) =>
        new(Manifest, correlationId, TenantId, parameters, Simulated: false);

    private static JsonElement CreateParameters(string targetUpn = TargetUpn) =>
        JsonSerializer.SerializeToElement(new
        {
            sourceUserUpn = SourceUpn,
            targetUserUpn = targetUpn,
            phoneNumber = PhoneNumber,
        });

    /// <summary>
    /// Mirrors the stage outputs that <c>tools/move-number-between-users/run.ps1</c>
    /// produces, so the engine contract is exercised without a tenant.
    /// </summary>
    private static FakeStageExecutor MoveExecutor(bool preflightPasses = true, bool verifyPasses = true) =>
        new((request, _) => Task.FromResult(request.Stage switch
        {
            ToolStage.Snapshot => StageExecutionResult.Success(JsonSerializer.SerializeToElement(new
            {
                phoneNumber = PhoneNumber,
                phoneNumberType = "CallingPlan",
                source = new { userPrincipalName = SourceUpn, lineUri = PhoneNumber, enterpriseVoiceEnabled = true },
                target = new { userPrincipalName = TargetUpn, lineUri = (string?)null, enterpriseVoiceEnabled = true },
            })),
            ToolStage.Preflight => StageExecutionResult.Success(JsonSerializer.SerializeToElement(new
            {
                summary = "Preflight complete.",
                checks = new[]
                {
                    new { check = "source user has the phone number assigned", passed = true, detail = "alice holds the number." },
                    new { check = "target user has no phone number assigned", passed = preflightPasses, detail = "bob is free." },
                },
            })),
            ToolStage.DryRun => StageExecutionResult.Success(JsonSerializer.SerializeToElement(new
            {
                summary = $"Would move {PhoneNumber} from {SourceUpn} to {TargetUpn}.",
                after = new
                {
                    phoneNumber = PhoneNumber,
                    plannedCommands = new[] { "Remove-CsPhoneNumberAssignment", "Set-CsPhoneNumberAssignment" },
                },
            })),
            ToolStage.Execute => StageExecutionResult.Success(JsonSerializer.SerializeToElement(new
            {
                summary = $"Moved {PhoneNumber} from {SourceUpn} to {TargetUpn}.",
                after = new
                {
                    phoneNumber = PhoneNumber,
                    source = new { userPrincipalName = SourceUpn, lineUri = (string?)null },
                    target = new { userPrincipalName = TargetUpn, lineUri = PhoneNumber },
                    changed = true,
                },
            })),
            ToolStage.Verify => StageExecutionResult.Success(JsonSerializer.SerializeToElement(new
            {
                summary = verifyPasses ? "Verified." : "1 verification check(s) failed after the move.",
                checks = new[]
                {
                    new { check = "number assigned to target", passed = verifyPasses, detail = "bob holds the number." },
                    new { check = "number released from source", passed = true, detail = "alice released the number." },
                },
            })),
            ToolStage.Rollback => StageExecutionResult.Success(JsonSerializer.SerializeToElement(new
            {
                summary = $"Rolled back: {PhoneNumber} was returned to {SourceUpn}.",
                after = new { restored = true },
            })),
            _ => StageExecutionResult.Failure(StageErrorCodes.ExecutionFailed, "Unexpected stage."),
        }));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InlineSessionManager : ITenantSessionManager
    {
        public Task<TResult> ExecuteAsync<TResult>(
            TenantSessionContext context,
            TenantOperationKind operationKind,
            Func<ITenantExecutionSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default) =>
            operation(new InlineSession(context), cancellationToken);

        private sealed class InlineSession(TenantSessionContext context) : ITenantExecutionSession
        {
            public TenantSessionContext Context { get; } = context;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
