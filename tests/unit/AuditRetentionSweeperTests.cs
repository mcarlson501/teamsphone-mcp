using Microsoft.Extensions.Logging.Abstractions;
using TeamsPhoneMcp.Audit;

namespace TeamsPhoneMcp.UnitTests;

public class AuditRetentionSweeperTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class RecordingSink : IAuditSink
    {
        public List<AuditRecord> Records { get; } = [];

        public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private static AuditRetentionSweeper CreateSweeper(TempAuditRoot root, RecordingSink sink) =>
        new(
            root.Resolver,
            sink,
            new TestOptionsMonitor<AuditOptions>(root.Options),
            new FixedTimeProvider(Now),
            NullLogger<AuditRetentionSweeper>.Instance);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static void SeedDay(TempAuditRoot root, string tenantId, DateTimeOffset day)
    {
        var file = root.Resolver.GetDailyFilePath(tenantId, day);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{}\n");

        var snapshots = root.Resolver.GetSnapshotDirectory(tenantId, day);
        Directory.CreateDirectory(snapshots);
        File.WriteAllText(Path.Combine(snapshots, "state-before.json"), "{}");
    }

    [Fact]
    public async Task SweepAsync_PrunesFilesOlderThanTheRetentionWindow()
    {
        using var root = new TempAuditRoot();
        root.Options.RetentionDays = 30;
        SeedDay(root, "contoso", Now.AddDays(-60));
        SeedDay(root, "contoso", Now.AddDays(-5));

        var sink = new RecordingSink();
        var result = await CreateSweeper(root, sink).SweepAsync();

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(1, result.DeletedSnapshotFolders);
        var remaining = Directory.GetFiles(root.Path, "*.jsonl", SearchOption.AllDirectories);
        Assert.Single(remaining);
        Assert.Equal("2026-03-09.jsonl", Path.GetFileName(remaining[0]));
    }

    [Fact]
    public async Task SweepAsync_RecordsThePruningItself()
    {
        using var root = new TempAuditRoot();
        root.Options.RetentionDays = 30;
        SeedDay(root, "contoso", Now.AddDays(-90));

        var sink = new RecordingSink();
        await CreateSweeper(root, sink).SweepAsync();

        var record = Assert.Single(sink.Records);
        Assert.Equal("audit-retention-sweep", record.ToolId);
        Assert.Equal(AuditPathResolver.SystemTenantFolder, record.TenantId);
        Assert.Equal("Succeeded", record.Status);
        Assert.Equal(1, record.Parameters!.Value.GetProperty("deletedFiles").GetInt32());
    }

    [Fact]
    public async Task SweepAsync_DoesNothingWhenEverythingIsInsideTheWindow()
    {
        using var root = new TempAuditRoot();
        SeedDay(root, "contoso", Now.AddDays(-1));

        var sink = new RecordingSink();
        var result = await CreateSweeper(root, sink).SweepAsync();

        Assert.Equal(0, result.DeletedFiles);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task SweepAsync_IgnoresFilesThatAreNotDateNamed()
    {
        using var root = new TempAuditRoot();
        root.Options.RetentionDays = 1;
        var tenantDirectory = root.Resolver.GetTenantDirectory("contoso");
        Directory.CreateDirectory(tenantDirectory);
        File.WriteAllText(Path.Combine(tenantDirectory, "notes.jsonl"), "{}");

        var sink = new RecordingSink();
        var result = await CreateSweeper(root, sink).SweepAsync();

        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(Path.Combine(tenantDirectory, "notes.jsonl")));
    }

    [Fact]
    public async Task SweepAsync_DoesNothingWhenAuditingIsDisabled()
    {
        using var root = new TempAuditRoot();
        root.Options.Enabled = false;
        root.Options.RetentionDays = 1;
        SeedDay(root, "contoso", Now.AddDays(-400));

        var sink = new RecordingSink();
        var result = await CreateSweeper(root, sink).SweepAsync();

        Assert.Equal(0, result.DeletedFiles);
        Assert.Single(Directory.GetFiles(root.Path, "*.jsonl", SearchOption.AllDirectories));
    }
}
