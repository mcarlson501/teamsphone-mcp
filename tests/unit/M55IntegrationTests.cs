using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using TeamsPhoneMcp.Host;
using Xunit.Abstractions;

namespace TeamsPhoneMcp.UnitTests;

/// <summary>
/// Gated live-tenant verification of the M5.5 diagnostics and reports. The test
/// is read-only and skips when the shared integration variables are not set.
/// </summary>
public sealed class M55IntegrationTests : IDisposable
{
    private const string BearerToken = "m55-integration-token";

    private static readonly Regex[] SecretShapes =
    {
        new("-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.IgnoreCase),
        new("\\b[0-9a-f]{40}\\b", RegexOptions.IgnoreCase),
        new("\\beyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\.", RegexOptions.None),
    };

    private readonly ITestOutputHelper _output;
    private readonly TempAuditRoot _auditRoot = new();

    public M55IntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose() => _auditRoot.Dispose();

    [Fact]
    public async Task DiagnosticsAndReports_SurfaceTenantFindings_AndAreFullyAudited()
    {
        var tenantId = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_TENANT_ID");
        var credentialRef = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_CREDENTIAL_REF");
        var userUpn = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_USER_UPN");
        var dialedNumber = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_DIALED_NUMBER");

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(credentialRef) ||
            string.IsNullOrWhiteSpace(userUpn))
        {
            return;
        }

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", BearerToken);
                builder.UseSetting("Audit:Enabled", "true");
                builder.UseSetting("Audit:RootPath", _auditRoot.Path);
            });
        await using var client = await CreateClientAsync(factory);

        var executedTools = new List<string>();
        var correlationIds = new List<string>();

        async Task<JsonElement> CallAndAssertAsync(
            string toolId,
            Dictionary<string, object?>? arguments = null)
        {
            arguments ??= [];
            arguments["tenantId"] = tenantId;
            arguments["credentialRef"] = credentialRef;

            var result = await client.CallToolAsync(toolId, arguments);
            var envelopeJson = result.StructuredContent?.GetRawText() ?? "<no structured content>";
            Assert.False(result.IsError, $"{toolId} returned an error.\nEnvelope: {envelopeJson}");
            Assert.NotNull(result.StructuredContent);

            var envelope = result.StructuredContent.Value;
            Assert.Equal("Succeeded", envelope.GetProperty("status").GetString());
            Assert.Equal(toolId, envelope.GetProperty("toolId").GetString());
            Assert.False(envelope.GetProperty("dryRun").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(envelope.GetProperty("summary").GetString()));
            Assert.True(envelope.TryGetProperty("timings", out _), $"{toolId} returned no timings.");
            Assert.True(
                envelope.GetProperty("diff").TryGetProperty("after", out var after) &&
                after.ValueKind != JsonValueKind.Null,
                $"{toolId} returned no diff.after payload.");

            var correlationId = envelope.GetProperty("correlationId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(correlationId));
            executedTools.Add(toolId);
            correlationIds.Add(correlationId!);
            _output.WriteLine($"{toolId}: {envelope.GetProperty("summary").GetString()}");
            return envelope;
        }

        var diagnosis = await CallAndAssertAsync(
            "diagnose-user-voice",
            new() { ["userUpn"] = userUpn });
        AssertActionableFindings(diagnosis, expectFinding: true);

        var numberUtilization = await CallAndAssertAsync("report-number-utilization");
        Assert.True(After(numberUtilization).GetProperty("total").GetInt32() >= 0);

        var licenseUtilization = await CallAndAssertAsync("report-license-utilization");
        Assert.True(After(licenseUtilization).GetProperty("observedUserCount").GetInt32() >= 0);

        var emergencyCoverage = await CallAndAssertAsync(
            "report-emergency-coverage",
            new() { ["pageSize"] = 200 });
        Assert.True(After(emergencyCoverage).GetProperty("enterpriseVoiceUserCount").GetInt32() >= 0);

        var policyAssignments = await CallAndAssertAsync(
            "report-policy-assignments",
            new() { ["format"] = "json", ["pageSize"] = 200 });
        Assert.Equal("json", After(policyAssignments).GetProperty("format").GetString());

        var health = await CallAndAssertAsync("run-tenant-health-check");
        AssertActionableFindings(health, expectFinding: true);

        if (string.IsNullOrWhiteSpace(dialedNumber))
        {
            var resourceAccounts = await CallAndAssertAsync(
                "list-resource-accounts",
                new() { ["pageSize"] = 200 });
            var phoneNumbers = await CallAndAssertAsync(
                "list-phone-numbers",
                new() { ["assignmentStatus"] = "assigned", ["pageSize"] = 200 });
            dialedNumber = FindAttachedResourceAccountNumber(resourceAccounts, phoneNumbers);
        }

        if (!string.IsNullOrWhiteSpace(dialedNumber))
        {
            var trace = await CallAndAssertAsync(
                "trace-call-flow",
                new() { ["dialedNumber"] = dialedNumber });
            Assert.NotEmpty(After(trace).GetProperty("nodes").EnumerateArray());
        }
        else
        {
            _output.WriteLine("trace-call-flow: skipped - no numbered resource account was found.");
        }

        var records = ReadAuditRecords();
        Assert.Equal(executedTools.Count, records.Count);
        foreach (var toolId in executedTools)
        {
            var record = Assert.Single(records, item => item.GetProperty("toolId").GetString() == toolId);
            Assert.Equal("Succeeded", record.GetProperty("status").GetString());
            Assert.Equal(0, record.GetProperty("riskTier").GetInt32());
            Assert.Contains(record.GetProperty("correlationId").GetString(), correlationIds);
        }

        var auditText = string.Join("\n", records.Select(record => record.GetRawText()));
        Assert.DoesNotContain(credentialRef, auditText, StringComparison.OrdinalIgnoreCase);
        foreach (var shape in SecretShapes)
        {
            Assert.False(shape.IsMatch(auditText), $"Audit trail matched a secret shape: {shape}.");
        }

        var pfxPassword = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_DEV_PFX_PASSWORD");
        if (!string.IsNullOrWhiteSpace(pfxPassword))
        {
            Assert.DoesNotContain(pfxPassword, auditText, StringComparison.Ordinal);
        }

        _output.WriteLine($"{records.Count} M5.5 audit records written under {_auditRoot.Path}.");
    }

    private static JsonElement After(JsonElement envelope) =>
        envelope.GetProperty("diff").GetProperty("after");

    private static void AssertActionableFindings(JsonElement envelope, bool expectFinding)
    {
        var findings = After(envelope).GetProperty("findings").EnumerateArray().ToArray();
        if (expectFinding)
        {
            Assert.NotEmpty(findings);
        }

        Assert.All(findings, finding =>
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("severity").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("code").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("what").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("why").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("fix").GetString()));
        });
    }

    private static string? FindAttachedResourceAccountNumber(
        JsonElement resourceAccountEnvelope,
        JsonElement phoneNumberEnvelope)
    {
        var accounts = After(resourceAccountEnvelope)
            .GetProperty("resourceAccounts")
            .EnumerateArray()
            .Where(account => account.GetProperty("attached").GetBoolean())
            .ToArray();

        foreach (var account in accounts)
        {
            if (account.TryGetProperty("phoneNumber", out var number) &&
                number.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(number.GetString()))
            {
                return number.GetString();
            }
        }

        var accountIds = accounts
            .Select(account => account.GetProperty("objectId").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var number in After(phoneNumberEnvelope).GetProperty("numbers").EnumerateArray())
        {
            if (number.TryGetProperty("assignedTo", out var target) &&
                target.ValueKind == JsonValueKind.String &&
                accountIds.Contains(target.GetString()))
            {
                return number.GetProperty("telephoneNumber").GetString();
            }
        }

        return null;
    }

    private static async Task<McpClient> CreateClientAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = factory.CreateClient();
        httpClient.Timeout = TimeSpan.FromMinutes(10);
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

    private List<JsonElement> ReadAuditRecords()
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
}