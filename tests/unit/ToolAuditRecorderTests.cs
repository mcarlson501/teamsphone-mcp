using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsPhoneMcp.Audit;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;

namespace TeamsPhoneMcp.UnitTests;

public class ToolAuditRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private sealed class RecordingSink : IAuditSink
    {
        public List<AuditRecord> Records { get; } = [];

        public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IAuditSink
    {
        public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default) =>
            throw new IOException("The audit disk is full.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ToolManifest CreateManifest(params string[] redactParams) => new()
    {
        Id = "mock-write-user-policy",
        Version = "1.2.3",
        Summary = "Test manifest.",
        Category = "write",
        RiskTier = 2,
        Annotations = new ToolManifestAnnotations { IdempotentHint = true },
        Inputs = [],
        RedactParams = [.. redactParams],
    };

    private static ToolResultEnvelope CreateEnvelope(
        ToolExecutionStatus status = ToolExecutionStatus.Succeeded,
        ToolError? error = null,
        ToolDiff? diff = null) => new()
        {
            Status = status,
            ToolId = "mock-write-user-policy",
            ToolVersion = "1.2.3",
            TenantId = Guid.Parse(TenantId),
            CorrelationId = "corr-1",
            DryRun = false,
            Summary = "Done.",
            Error = error,
            Diff = diff,
            Timings = new ToolTimings(42, new Dictionary<string, long> { ["execute"] = 40 }),
            Preflight = [new ToolCheckResult("userExists", true, null)],
            Verification = [new ToolCheckResult("policyApplied", false, "still pending")],
        };

    private static (ToolAuditRecorder Recorder, RecordingSink Sink) CreateRecorder(
        IAuditSnapshotStore? snapshotStore = null)
    {
        var sink = new RecordingSink();
        var recorder = new ToolAuditRecorder(
            sink,
            snapshotStore ?? new NullAuditSnapshotStore(),
            new FixedTimeProvider(Now),
            NullLogger<ToolAuditRecorder>.Instance);
        return (recorder, sink);
    }

    private static ToolAuditContext CreateContext(ToolManifest manifest, object? parameters = null) =>
        new(
            manifest,
            "corr-1",
            TenantId,
            JsonSerializer.SerializeToElement(parameters ?? new { targetUserUpn = "user@contoso.com" }),
            Simulated: false)
        {
            SessionId = "session-9",
            ClientId = "vscode/1.0",
        };

    [Fact]
    public async Task RecordAsync_ProjectsTheEnvelopeIntoOneRecord()
    {
        var (recorder, sink) = CreateRecorder();
        var manifest = CreateManifest();

        await recorder.RecordAsync(CreateContext(manifest), CreateEnvelope());

        var record = Assert.Single(sink.Records);
        Assert.Equal(Now, record.Timestamp);
        Assert.Equal("corr-1", record.CorrelationId);
        Assert.Equal("session-9", record.SessionId);
        Assert.Equal("vscode/1.0", record.ClientId);
        Assert.Equal(TenantId, record.TenantId);
        Assert.Equal("mock-write-user-policy", record.ToolId);
        Assert.Equal("1.2.3", record.ToolVersion);
        Assert.Equal("Succeeded", record.Status);
        Assert.Equal(2, record.RiskTier);
        Assert.Equal(42, record.DurationMs);
        Assert.Equal("execute", Assert.Single(record.Stages).Stage);
        Assert.Collection(
            record.Checks,
            check => Assert.Equal("preflight", check.Phase),
            check =>
            {
                Assert.Equal("verify", check.Phase);
                Assert.False(check.Passed);
            });
    }

    [Fact]
    public async Task RecordAsync_RedactsManifestDeclaredParameters()
    {
        var (recorder, sink) = CreateRecorder();
        var manifest = CreateManifest("applicationSecret");

        await recorder.RecordAsync(
            CreateContext(manifest, new { targetUserUpn = "user@contoso.com", applicationSecret = "hunter2" }),
            CreateEnvelope());

        var parameters = Assert.Single(sink.Records).Parameters!.Value;
        Assert.Equal("user@contoso.com", parameters.GetProperty("targetUserUpn").GetString());
        Assert.Equal(AuditRedactor.RedactedPlaceholder, parameters.GetProperty("applicationSecret").GetString());
    }

    [Fact]
    public async Task RecordAsync_CapturesFailuresIncludingTheErrorCode()
    {
        var (recorder, sink) = CreateRecorder();

        await recorder.RecordAsync(
            CreateContext(CreateManifest()),
            CreateEnvelope(ToolExecutionStatus.Failed, new ToolError("confirmationRequired", "A token is required.")));

        var record = Assert.Single(sink.Records);
        Assert.Equal("Failed", record.Status);
        Assert.Equal("confirmationRequired", record.ErrorCode);
        Assert.Equal("A token is required.", record.ErrorMessage);
    }

    [Fact]
    public async Task RecordAsync_StoresBeforeAndAfterSnapshotsAsSeparateArtifacts()
    {
        using var root = new TempAuditRoot();
        var snapshotStore = new FileAuditSnapshotStore(
            root.Resolver,
            new TestOptionsMonitor<AuditOptions>(root.Options),
            NullLogger<FileAuditSnapshotStore>.Instance);
        var (recorder, sink) = CreateRecorder(snapshotStore);

        var diff = new ToolDiff(
            JsonSerializer.SerializeToElement(new { policy = "old" }),
            JsonSerializer.SerializeToElement(new { policy = "new" }));

        await recorder.RecordAsync(CreateContext(CreateManifest()), CreateEnvelope(diff: diff));

        var record = Assert.Single(sink.Records);
        Assert.NotNull(record.SnapshotRefs);
        Assert.EndsWith("corr-1-before.json", record.SnapshotRefs!.Before, StringComparison.Ordinal);
        Assert.EndsWith("corr-1-after.json", record.SnapshotRefs.After, StringComparison.Ordinal);

        var beforePath = Path.Combine(root.Path, record.SnapshotRefs.Before!);
        Assert.Contains("\"old\"", await File.ReadAllTextAsync(beforePath));
    }

    [Fact]
    public async Task RecordAsync_OmitsSnapshotRefsWhenThereIsNoDiff()
    {
        using var root = new TempAuditRoot();
        var snapshotStore = new FileAuditSnapshotStore(
            root.Resolver,
            new TestOptionsMonitor<AuditOptions>(root.Options),
            NullLogger<FileAuditSnapshotStore>.Instance);
        var (recorder, sink) = CreateRecorder(snapshotStore);

        await recorder.RecordAsync(CreateContext(CreateManifest()), CreateEnvelope());

        Assert.Null(Assert.Single(sink.Records).SnapshotRefs);
    }

    [Fact]
    public async Task RecordAsync_SwallowsSinkFailuresSoTenantOperationsStillSucceed()
    {
        var recorder = new ToolAuditRecorder(
            new ThrowingSink(),
            new NullAuditSnapshotStore(),
            new FixedTimeProvider(Now),
            NullLogger<ToolAuditRecorder>.Instance);

        var exception = await Record.ExceptionAsync(() =>
            recorder.RecordAsync(CreateContext(CreateManifest()), CreateEnvelope()));

        Assert.Null(exception);
    }
}
