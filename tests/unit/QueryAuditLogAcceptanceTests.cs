using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using TeamsPhoneMcp.Audit;
using TeamsPhoneMcp.Host;

namespace TeamsPhoneMcp.UnitTests;

public sealed class QueryAuditLogAcceptanceTests : IDisposable
{
    private const string BearerToken = "query-audit-token";
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private readonly TempAuditRoot _auditRoot = new();

    public void Dispose() => _auditRoot.Dispose();

    [Fact]
    public async Task QueryAuditLog_FiltersAndPaginatesWithFilterBoundTokens()
    {
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var sink = new JsonlAuditSink(
            _auditRoot.Resolver,
            new TestOptionsMonitor<AuditOptions>(_auditRoot.Options),
            NullLogger<JsonlAuditSink>.Instance);
        await sink.WriteAsync(CreateRecord(start.AddHours(1), "move-number-between-users", "Succeeded"));
        await sink.WriteAsync(CreateRecord(start.AddHours(2), "ping", "Succeeded"));
        await sink.WriteAsync(CreateRecord(start.AddHours(3), "move-number-between-users", "Failed"));

        await using var factory = CreateFactory();
        await using var client = await CreateClientAsync(factory);
        var arguments = new Dictionary<string, object?>
        {
            ["tenantId"] = TenantId,
            ["toolId"] = "move-number-between-users",
            ["pageSize"] = 1,
        };

        var firstResult = await client.CallToolAsync("query-audit-log", arguments);
        Assert.NotEqual(true, firstResult.IsError);
        var first = firstResult.StructuredContent!.Value;
        var firstRecord = Assert.Single(first.GetProperty("records").EnumerateArray());
        Assert.Equal("Failed", firstRecord.GetProperty("status").GetString());
        Assert.Equal(2, first.GetProperty("totalCount").GetInt32());
        Assert.True(first.GetProperty("pagination").GetProperty("hasMore").GetBoolean());
        var token = first.GetProperty("pagination").GetProperty("continuationToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var secondResult = await client.CallToolAsync(
            "query-audit-log",
            new Dictionary<string, object?>(arguments) { ["continuationToken"] = token });
        Assert.NotEqual(true, secondResult.IsError);
        var second = secondResult.StructuredContent!.Value;
        var secondRecord = Assert.Single(second.GetProperty("records").EnumerateArray());
        Assert.Equal("Succeeded", secondRecord.GetProperty("status").GetString());
        Assert.False(second.GetProperty("pagination").GetProperty("hasMore").GetBoolean());

        var changedFilterResult = await client.CallToolAsync(
            "query-audit-log",
            new Dictionary<string, object?>(arguments)
            {
                ["status"] = "Succeeded",
                ["continuationToken"] = token,
            });
        Assert.True(changedFilterResult.IsError);
        Assert.Equal(
            "invalidContinuationToken",
            changedFilterResult.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task QueryAuditLog_RejectsInvalidDateRangesWithoutReadingTenantData()
    {
        await using var factory = CreateFactory();
        await using var client = await CreateClientAsync(factory);

        var result = await client.CallToolAsync(
            "query-audit-log",
            new Dictionary<string, object?>
            {
                ["tenantId"] = TenantId,
                ["fromUtc"] = "not-a-date",
            });

        Assert.True(result.IsError);
        Assert.Equal(
            "invalidDateRange",
            result.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task GetChangeDetail_ReturnsTheRecordAndStoredSnapshots()
    {
        var timestamp = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var requestedCorrelationId = Guid.NewGuid().ToString();
        var snapshotStore = new FileAuditSnapshotStore(
            _auditRoot.Resolver,
            new TestOptionsMonitor<AuditOptions>(_auditRoot.Options),
            NullLogger<FileAuditSnapshotStore>.Instance);
        var refs = await snapshotStore.StoreAsync(
            TenantId,
            requestedCorrelationId,
            timestamp,
            JsonSerializer.SerializeToElement(new { number = "+15550000001" }),
            JsonSerializer.SerializeToElement(new { number = "+15550000002" }));
        var sink = new JsonlAuditSink(
            _auditRoot.Resolver,
            new TestOptionsMonitor<AuditOptions>(_auditRoot.Options),
            NullLogger<JsonlAuditSink>.Instance);
        await sink.WriteAsync(
            CreateRecord(timestamp, "move-number-between-users", "Succeeded") with
            {
                CorrelationId = requestedCorrelationId,
                SnapshotRefs = refs,
            });

        await using var factory = CreateFactory();
        await using var client = await CreateClientAsync(factory);
        var result = await client.CallToolAsync(
            "get-change-detail",
            new Dictionary<string, object?>
            {
                ["tenantId"] = TenantId,
                ["correlationId"] = requestedCorrelationId,
            });

        Assert.NotEqual(true, result.IsError);
        var payload = result.StructuredContent!.Value;
        Assert.Equal(requestedCorrelationId, payload.GetProperty("requestedCorrelationId").GetString());
        Assert.NotEqual(requestedCorrelationId, payload.GetProperty("correlationId").GetString());
        var detail = payload.GetProperty("detail");
        Assert.Equal("move-number-between-users", detail.GetProperty("record").GetProperty("toolId").GetString());
        Assert.Equal("+15550000001", detail.GetProperty("before").GetProperty("number").GetString());
        Assert.Equal("+15550000002", detail.GetProperty("after").GetProperty("number").GetString());
    }

    [Theory]
    [InlineData("export-audit-report", "markdown", "# Teams Phone change history")]
    [InlineData("export-audit-report", "csv", "timestampUtc,correlationId")]
    [InlineData("report-change-history", "markdown", "# Teams Phone change history")]
    [InlineData("report-change-history", "csv", "timestampUtc,correlationId")]
    public async Task AuditReportTools_RenderTheRequestedFormat(string toolId, string format, string expectedText)
    {
        var timestamp = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var sink = new JsonlAuditSink(
            _auditRoot.Resolver,
            new TestOptionsMonitor<AuditOptions>(_auditRoot.Options),
            NullLogger<JsonlAuditSink>.Instance);
        await sink.WriteAsync(CreateRecord(timestamp, "move-number-between-users", "Succeeded"));

        await using var factory = CreateFactory();
        await using var client = await CreateClientAsync(factory);
        var result = await client.CallToolAsync(
            toolId,
            new Dictionary<string, object?>
            {
                ["tenantId"] = TenantId,
                ["fromUtc"] = "2026-08-01T00:00:00Z",
                ["toUtc"] = "2026-08-02T00:00:00Z",
                ["format"] = format,
            });

        Assert.NotEqual(true, result.IsError);
        var payload = result.StructuredContent!.Value;
        Assert.Equal(1, payload.GetProperty("recordCount").GetInt32());
        Assert.Contains(expectedText, payload.GetProperty("report").GetString());
        Assert.Contains("move-number-between-users", payload.GetProperty("report").GetString());
    }

    private TestServerHost CreateFactory() =>
        new(builder =>
        {
            builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", BearerToken);
            builder.UseSetting("Audit:RootPath", _auditRoot.Path);
            builder.UseSetting("Audit:Enabled", "true");
        });

    private static async Task<McpClient> CreateClientAsync(TestServerHost factory)
    {
        var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport);
    }

    private static AuditRecord CreateRecord(DateTimeOffset timestamp, string toolId, string status) =>
        new()
        {
            Timestamp = timestamp,
            CorrelationId = Guid.NewGuid().ToString(),
            ClientId = "inspector/1.0",
            TenantId = TenantId,
            ToolId = toolId,
            ToolVersion = "1.0.0",
            Status = status,
        };
}