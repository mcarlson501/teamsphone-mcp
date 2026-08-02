using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsPhoneMcp.Audit;

namespace TeamsPhoneMcp.UnitTests;

public class JsonlAuditSinkTests
{
    private static AuditRecord CreateRecord(
        string tenantId = "11111111-1111-1111-1111-111111111111",
        DateTimeOffset? timestamp = null,
        string toolId = "list-phone-numbers") =>
        new()
        {
            Timestamp = timestamp ?? new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero),
            CorrelationId = Guid.NewGuid().ToString(),
            TenantId = tenantId,
            ToolId = toolId,
            ToolVersion = "1.0.0",
            Status = "Succeeded",
        };

    [Fact]
    public async Task WriteAsync_CreatesOneFilePerTenantPerDay()
    {
        using var root = new TempAuditRoot();
        var sink = new JsonlAuditSink(
            root.Resolver,
            new TestOptionsMonitor<AuditOptions>(root.Options),
            NullLogger<JsonlAuditSink>.Instance);

        var day = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);
        await sink.WriteAsync(CreateRecord(timestamp: day));
        await sink.WriteAsync(CreateRecord(timestamp: day.AddHours(6)));
        await sink.WriteAsync(CreateRecord(timestamp: day.AddDays(1)));

        var tenantDirectory = root.Resolver.GetTenantDirectory("11111111-1111-1111-1111-111111111111");
        var files = Directory.GetFiles(tenantDirectory, "*.jsonl").Order(StringComparer.Ordinal).ToList();

        Assert.Equal(2, files.Count);
        Assert.Equal("2026-03-14.jsonl", Path.GetFileName(files[0]));
        Assert.Equal("2026-03-15.jsonl", Path.GetFileName(files[1]));
        Assert.Equal(2, File.ReadAllLines(files[0]).Length);
    }

    [Fact]
    public async Task WriteAsync_AppendsOneParsableJsonObjectPerLine()
    {
        using var root = new TempAuditRoot();
        var sink = new JsonlAuditSink(
            root.Resolver,
            new TestOptionsMonitor<AuditOptions>(root.Options),
            NullLogger<JsonlAuditSink>.Instance);

        await sink.WriteAsync(CreateRecord(toolId: "ping"));
        await sink.WriteAsync(CreateRecord(toolId: "get-schedules"));

        var file = Directory.GetFiles(root.Path, "*.jsonl", SearchOption.AllDirectories).Single();
        var lines = await File.ReadAllLinesAsync(file);

        Assert.Equal(2, lines.Length);
        var parsed = lines.Select(line => JsonDocument.Parse(line).RootElement).ToList();
        Assert.Equal("ping", parsed[0].GetProperty("toolId").GetString());
        Assert.Equal("get-schedules", parsed[1].GetProperty("toolId").GetString());
        Assert.Equal(1, parsed[0].GetProperty("recordVersion").GetInt32());
    }

    [Fact]
    public async Task WriteAsync_SeparatesTenantsIntoTheirOwnFolders()
    {
        using var root = new TempAuditRoot();
        var sink = new JsonlAuditSink(
            root.Resolver,
            new TestOptionsMonitor<AuditOptions>(root.Options),
            NullLogger<JsonlAuditSink>.Instance);

        await sink.WriteAsync(CreateRecord(tenantId: "11111111-1111-1111-1111-111111111111"));
        await sink.WriteAsync(CreateRecord(tenantId: "22222222-2222-2222-2222-222222222222"));

        Assert.Equal(2, Directory.GetDirectories(root.Path).Length);
    }

    [Fact]
    public async Task WriteAsync_ContainsPathTraversalInTheTenantIdentifier()
    {
        using var root = new TempAuditRoot();
        var sink = new JsonlAuditSink(
            root.Resolver,
            new TestOptionsMonitor<AuditOptions>(root.Options),
            NullLogger<JsonlAuditSink>.Instance);

        await sink.WriteAsync(CreateRecord(tenantId: "../../etc/passwd"));

        var written = Directory.GetFiles(root.Path, "*.jsonl", SearchOption.AllDirectories);
        Assert.Single(written);
        Assert.StartsWith(root.Path, Path.GetFullPath(written[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_WritesNothingWhenAuditingIsDisabled()
    {
        using var root = new TempAuditRoot();
        root.Options.Enabled = false;
        var sink = new JsonlAuditSink(
            root.Resolver,
            new TestOptionsMonitor<AuditOptions>(root.Options),
            NullLogger<JsonlAuditSink>.Instance);

        await sink.WriteAsync(CreateRecord());

        Assert.Empty(Directory.GetFiles(root.Path, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WriteAsync_IsSafeUnderConcurrentWritersToTheSameFile()
    {
        using var root = new TempAuditRoot();
        var sink = new JsonlAuditSink(
            root.Resolver,
            new TestOptionsMonitor<AuditOptions>(root.Options),
            NullLogger<JsonlAuditSink>.Instance);

        await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(_ => sink.WriteAsync(CreateRecord()).AsTask()));

        var file = Directory.GetFiles(root.Path, "*.jsonl", SearchOption.AllDirectories).Single();
        var lines = await File.ReadAllLinesAsync(file);

        Assert.Equal(25, lines.Length);
        Assert.All(lines, line => JsonDocument.Parse(line));
    }
}
