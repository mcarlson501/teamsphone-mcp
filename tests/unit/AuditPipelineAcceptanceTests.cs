using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using TeamsPhoneMcp.Audit;
using TeamsPhoneMcp.Host;

namespace TeamsPhoneMcp.UnitTests;

/// <summary>
/// End-to-end proof of the M3 audit acceptance criterion: every call — including
/// a forced failure — produces a valid audit record, and no secret material ever
/// reaches the trail (build spec §9, §10 log scrubber).
/// </summary>
public sealed class AuditPipelineAcceptanceTests : IDisposable
{
    private const string BearerToken = "audit-token-abc123";
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private readonly TempAuditRoot _auditRoot = new();

    public void Dispose() => _auditRoot.Dispose();

    private async Task<McpClient> CreateClientAsync(TestServerHost factory)
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

    private TestServerHost CreateFactory() =>
        new(builder =>
        {
            builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", BearerToken);
            builder.UseSetting("Audit:RootPath", _auditRoot.Path);
            builder.UseSetting("Audit:Enabled", "true");
        });

    private IReadOnlyList<JsonElement> ReadAuditRecords()
    {
        var records = new List<JsonElement>();
        if (!Directory.Exists(_auditRoot.Path))
        {
            return records;
        }

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

    private static void AssertIsValidRecord(JsonElement record)
    {
        Assert.Equal(1, record.GetProperty("recordVersion").GetInt32());
        Assert.True(record.TryGetProperty("timestamp", out _));
        Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("correlationId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("tenantId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("toolId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("toolVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task SuccessfulCall_ProducesOneValidAuditRecord()
    {
        await using var factory = CreateFactory();
        await using var client = await CreateClientAsync(factory);

        await client.CallToolAsync("ping", new Dictionary<string, object?> { ["message"] = "hello" });

        var record = Assert.Single(ReadAuditRecords());
        AssertIsValidRecord(record);
        Assert.Equal("ping", record.GetProperty("toolId").GetString());
        Assert.Equal("Succeeded", record.GetProperty("status").GetString());
        Assert.Equal("hello", record.GetProperty("parameters").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ForcedFailure_StillProducesAValidAuditRecordWithTheErrorCode()
    {
        await using var factory = CreateFactory();
        await using var client = await CreateClientAsync(factory);

        // A tampered continuation token fails before the tenant session is touched.
        await client.CallToolAsync(
            "list-phone-numbers",
            new Dictionary<string, object?>
            {
                ["tenantId"] = TenantId,
                ["credentialRef"] = "contoso",
                ["continuationToken"] = "not-a-real-token",
            });

        var record = Assert.Single(ReadAuditRecords());
        AssertIsValidRecord(record);
        Assert.Equal("list-phone-numbers", record.GetProperty("toolId").GetString());
        Assert.Equal("Failed", record.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("errorCode").GetString()));
        Assert.Equal(TenantId, record.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task PolicyRejectedWrite_IsAudited()
    {
        await using var factory = CreateFactory();
        await using var client = await CreateClientAsync(factory);

        // Executing without a confirmation token is rejected by the write policy.
        await client.CallToolAsync(
            "mock-write-user-policy",
            new Dictionary<string, object?>
            {
                ["tenantId"] = TenantId,
                ["targetUserUpn"] = "user@contoso.com",
                ["policyName"] = "US-Calling",
                ["dryRun"] = false,
            });

        var record = Assert.Single(ReadAuditRecords());
        AssertIsValidRecord(record);
        Assert.Equal("mock-write-user-policy", record.GetProperty("toolId").GetString());
        Assert.Equal("Failed", record.GetProperty("status").GetString());
        Assert.Equal("missingConfirmationToken", record.GetProperty("errorCode").GetString());
        Assert.Equal(2, record.GetProperty("riskTier").GetInt32());
    }

    [Fact]
    public async Task EveryCallLandsInTheTenantsDailyFile()
    {
        await using var factory = CreateFactory();
        await using var client = await CreateClientAsync(factory);

        for (var i = 0; i < 3; i++)
        {
            await client.CallToolAsync("ping", new Dictionary<string, object?> { ["message"] = $"call-{i}" });
        }

        Assert.Equal(3, ReadAuditRecords().Count);
        var files = Directory.GetFiles(_auditRoot.Path, "*.jsonl", SearchOption.AllDirectories);
        Assert.Single(files);
        Assert.EndsWith(".jsonl", files[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The build spec §10 log scrubber check: secret-shaped material handed to the
    /// server must be unreadable everywhere in the audit store.
    /// </summary>
    [Fact]
    public async Task SecretShapedInput_NeverReachesTheAuditStore()
    {
        const string Thumbprint = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";
        const string PrivateKey = "-----BEGIN RSA PRIVATE KEY-----MIIEowIBAAKCAQEA-----END RSA PRIVATE KEY-----";

        await using var factory = CreateFactory();
        await using var client = await CreateClientAsync(factory);

        await client.CallToolAsync(
            "ping",
            new Dictionary<string, object?> { ["message"] = $"thumbprint {Thumbprint} key {PrivateKey}" });

        var contents = Directory
            .EnumerateFiles(_auditRoot.Path, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();

        Assert.NotEmpty(contents);
        Assert.All(contents, content =>
        {
            Assert.DoesNotContain(Thumbprint, content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(AuditRedactor.RedactedPlaceholder, content, StringComparison.Ordinal);
        });
    }
}
