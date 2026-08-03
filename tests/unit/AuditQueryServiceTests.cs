using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using TeamsPhoneMcp.Audit;

namespace TeamsPhoneMcp.UnitTests;

public sealed class AuditQueryServiceTests
{
    [Fact]
    public async Task QueryAsync_FiltersAndOrdersMatchingRecordsNewestFirst()
    {
        using var root = new TempAuditRoot();
        var sink = CreateSink(root);
        var service = CreateService(root);
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        await sink.WriteAsync(CreateRecord(start.AddHours(1), "move-number-between-users", "Succeeded", "inspector"));
        await sink.WriteAsync(CreateRecord(start.AddHours(2), "move-number-between-users", "Failed", "inspector"));
        await sink.WriteAsync(CreateRecord(start.AddHours(3), "move-number-between-users", "Succeeded", "automation"));
        await sink.WriteAsync(CreateRecord(start.AddDays(2), "move-number-between-users", "Succeeded", "inspector"));
        await sink.WriteAsync(CreateRecord(start.AddHours(4), "ping", "Succeeded", "inspector"));

        var page = await service.QueryAsync(new AuditQuery(TenantId)
        {
            FromUtc = start,
            ToUtc = start.AddDays(1),
            ToolId = "MOVE-NUMBER-BETWEEN-USERS",
            Status = "succeeded",
            ClientId = "Inspector",
        });

        var record = Assert.Single(page.Records);
        Assert.Equal(start.AddHours(1), record.Timestamp);
        Assert.Equal(1, page.TotalCount);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task QueryAsync_PaginatesAfterNewestFirstOrdering()
    {
        using var root = new TempAuditRoot();
        var sink = CreateSink(root);
        var service = CreateService(root);
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        await sink.WriteAsync(CreateRecord(start.AddHours(1)));
        await sink.WriteAsync(CreateRecord(start.AddHours(2)));
        await sink.WriteAsync(CreateRecord(start.AddHours(3)));

        var page = await service.QueryAsync(new AuditQuery(TenantId) { Offset = 1, Limit = 1 });

        var record = Assert.Single(page.Records);
        Assert.Equal(start.AddHours(2), record.Timestamp);
        Assert.Equal(3, page.TotalCount);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task QueryAsync_SkipsMalformedLines()
    {
        using var root = new TempAuditRoot();
        var sink = CreateSink(root);
        var service = CreateService(root);
        var timestamp = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        await sink.WriteAsync(CreateRecord(timestamp));
        var file = root.Resolver.GetDailyFilePath(TenantId, timestamp);
        await File.AppendAllTextAsync(file, "{not-json}" + Environment.NewLine);

        var page = await service.QueryAsync(new AuditQuery(TenantId));

        Assert.Single(page.Records);
    }

    [Fact]
    public async Task QueryAsync_DoesNotCrossTenantsWithCollidingSanitizedPaths()
    {
        using var root = new TempAuditRoot();
        var sink = CreateSink(root);
        var service = CreateService(root);
        var timestamp = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        await sink.WriteAsync(CreateRecord(timestamp) with { TenantId = "tenant/a" });

        var page = await service.QueryAsync(new AuditQuery("tenant?a"));

        Assert.Empty(page.Records);
    }

    [Fact]
    public async Task GetChangeDetailAsync_ReturnsRecordAndTenantContainedSnapshots()
    {
        using var root = new TempAuditRoot();
        var timestamp = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var correlationId = Guid.NewGuid().ToString();
        var snapshotStore = new FileAuditSnapshotStore(
            root.Resolver,
            new TestOptionsMonitor<AuditOptions>(root.Options),
            NullLogger<FileAuditSnapshotStore>.Instance);
        var before = JsonSerializer.SerializeToElement(new { state = "before" });
        var after = JsonSerializer.SerializeToElement(new { state = "after" });
        var refs = await snapshotStore.StoreAsync(TenantId, correlationId, timestamp, before, after);
        await CreateSink(root).WriteAsync(
            CreateRecord(timestamp) with { CorrelationId = correlationId, SnapshotRefs = refs });

        var detail = await CreateService(root).GetChangeDetailAsync(TenantId, correlationId);

        Assert.NotNull(detail);
        Assert.Equal(correlationId, detail.Record.CorrelationId);
        Assert.Equal("before", detail.Before!.Value.GetProperty("state").GetString());
        Assert.Equal("after", detail.After!.Value.GetProperty("state").GetString());
    }

    [Fact]
    public async Task GetChangeDetailAsync_DoesNotFollowSnapshotReferencesOutsideTenantDirectory()
    {
        using var root = new TempAuditRoot();
        var timestamp = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var correlationId = Guid.NewGuid().ToString();
        var otherTenant = "22222222-2222-2222-2222-222222222222";
        var otherDirectory = root.Resolver.GetSnapshotDirectory(otherTenant, timestamp);
        Directory.CreateDirectory(otherDirectory);
        var otherPath = Path.Combine(otherDirectory, "private.json");
        await File.WriteAllTextAsync(otherPath, "{\"secret\":true}");
        var outsideRef = Path.GetRelativePath(root.Path, otherPath).Replace(Path.DirectorySeparatorChar, '/');
        await CreateSink(root).WriteAsync(
            CreateRecord(timestamp) with
            {
                CorrelationId = correlationId,
                SnapshotRefs = new AuditSnapshotRefs(outsideRef, null),
            });

        var detail = await CreateService(root).GetChangeDetailAsync(TenantId, correlationId);

        Assert.NotNull(detail);
        Assert.Null(detail.Before);
    }

    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private static FileAuditQueryService CreateService(TempAuditRoot root) =>
        new(root.Resolver, NullLogger<FileAuditQueryService>.Instance);

    private static JsonlAuditSink CreateSink(TempAuditRoot root) =>
        new(
            root.Resolver,
            new TestOptionsMonitor<AuditOptions>(root.Options),
            NullLogger<JsonlAuditSink>.Instance);

    private static AuditRecord CreateRecord(
        DateTimeOffset timestamp,
        string toolId = "ping",
        string status = "Succeeded",
        string clientId = "inspector") =>
        new()
        {
            Timestamp = timestamp,
            CorrelationId = Guid.NewGuid().ToString(),
            ClientId = clientId,
            TenantId = TenantId,
            ToolId = toolId,
            ToolVersion = "1.0.0",
            Status = status,
        };
}