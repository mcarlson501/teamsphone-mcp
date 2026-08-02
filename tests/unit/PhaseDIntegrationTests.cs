using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using TeamsPhoneMcp.Host;
using Xunit.Abstractions;

namespace TeamsPhoneMcp.UnitTests;

/// <summary>
/// Gated live-tenant sign-off for the M5 composites. It offboards an explicitly
/// isolated numbered user, then onboards that user from the captured snapshot in a
/// finally block so the original number and voice configuration are restored.
/// </summary>
public sealed class PhaseDIntegrationTests : IDisposable
{
    private const string BearerToken = "phase-d-integration-token";

    private readonly ITestOutputHelper _output;
    private readonly TempAuditRoot _auditRoot = new();

    public PhaseDIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose() => _auditRoot.Dispose();

    [Fact]
    public async Task OffboardThenOnboard_RestoresNumberedUserAndInventory()
    {
        var tenantId = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_TENANT_ID");
        var credentialRef = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_CREDENTIAL_REF");
        var userUpn = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_PHASE_D_USER_UPN") ??
            Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_MOVE_SOURCE_UPN");

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

        var baselineEnvelope = await GetUserVoiceConfigAsync(client, tenantId, credentialRef, userUpn);
        Assert.Equal("Succeeded", baselineEnvelope.GetProperty("status").GetString());
        var baseline = baselineEnvelope.GetProperty("diff").GetProperty("after").Clone();
        Assert.True(baseline.GetProperty("enterpriseVoiceEnabled").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(GetOptionalString(baseline, "lineUri")));

        var offboardArguments = new Dictionary<string, object?>
        {
            ["tenantId"] = tenantId,
            ["credentialRef"] = credentialRef,
            ["userUpn"] = userUpn,
        };
        var offboardDryRun = await CallAsync(client, "offboard-voice-user", offboardArguments);
        AssertStatus(offboardDryRun, "DryRunCompleted");
        AssertAllChecksPassed(offboardDryRun, "preflight");

        var captured = offboardDryRun.GetProperty("diff").GetProperty("before");
        var capturedUser = captured.GetProperty("user");
        var capturedNumber = captured.GetProperty("numberSnapshot");
        Assert.Empty(captured.GetProperty("queueMemberships").EnumerateArray());
        Assert.Null(GetOptionalString(capturedUser, "callerIdPolicy"));

        var phoneNumber = GetOptionalString(capturedNumber, "phoneNumber");
        var locationId = GetOptionalString(capturedNumber, "locationId");
        Assert.False(string.IsNullOrWhiteSpace(phoneNumber));
        Assert.False(string.IsNullOrWhiteSpace(locationId));

        var onboardArguments = new Dictionary<string, object?>
        {
            ["tenantId"] = tenantId,
            ["credentialRef"] = credentialRef,
            ["userUpn"] = userUpn,
            ["phoneNumber"] = phoneNumber,
            ["emergencyLocationId"] = locationId,
        };
        CopyOptionalString(capturedUser, onboardArguments, "onlineVoiceRoutingPolicy");
        CopyOptionalString(capturedUser, onboardArguments, "tenantDialPlan");
        CopyOptionalString(capturedUser, onboardArguments, "teamsCallingPolicy");

        var offboarded = false;
        try
        {
            var token = offboardDryRun.GetProperty("confirmationToken").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));
            var executeArguments = new Dictionary<string, object?>(offboardArguments)
            {
                ["dryRun"] = false,
                ["confirmationToken"] = token,
            };

            var execute = await CallAsync(client, "offboard-voice-user", executeArguments);
            offboarded = string.Equals(execute.GetProperty("status").GetString(), "Succeeded", StringComparison.Ordinal);
            Assert.True(offboarded, execute.GetRawText());
            AssertAllChecksPassed(execute, "verification");
            var report = execute.GetProperty("diff").GetProperty("after").GetProperty("disposition");
            Assert.Equal("releasedToTenantInventory", report.GetProperty("numberDisposition").GetString());
            Assert.Empty(report.GetProperty("removedQueueNames").EnumerateArray());
            _output.WriteLine(execute.GetProperty("summary").GetString());
        }
        finally
        {
            if (offboarded)
            {
                var restored = await ConfirmedCallAsync(client, "onboard-voice-user", onboardArguments);
                AssertAllChecksPassed(restored, "verification");
                _output.WriteLine(restored.GetProperty("summary").GetString());
            }
        }

        var restoredEnvelope = await GetUserVoiceConfigAsync(client, tenantId, credentialRef, userUpn);
        Assert.Equal("Succeeded", restoredEnvelope.GetProperty("status").GetString());
        var restoredUser = restoredEnvelope.GetProperty("diff").GetProperty("after");
        foreach (var propertyName in new[]
                 {
                     "enterpriseVoiceEnabled",
                     "lineUri",
                     "onlineVoiceRoutingPolicy",
                     "tenantDialPlan",
                     "teamsCallingPolicy",
                 })
        {
            Assert.Equal(baseline.GetProperty(propertyName).GetRawText(), restoredUser.GetProperty(propertyName).GetRawText());
        }

        var writeRecords = ReadAuditRecords()
            .Where(record => record.GetProperty("riskTier").GetInt32() >= 1)
            .ToList();
        Assert.Equal(4, writeRecords.Count);
        Assert.Equal(2, writeRecords.Count(record => record.GetProperty("dryRun").GetBoolean()));
        Assert.Contains(writeRecords, record => record.GetProperty("toolId").GetString() == "onboard-voice-user");
        Assert.Contains(writeRecords, record => record.GetProperty("toolId").GetString() == "offboard-voice-user");
    }

    private static async Task<JsonElement> ConfirmedCallAsync(
        McpClient client,
        string toolId,
        Dictionary<string, object?> arguments)
    {
        var dryRun = await CallAsync(client, toolId, arguments);
        AssertStatus(dryRun, "DryRunCompleted");
        AssertAllChecksPassed(dryRun, "preflight");

        var token = dryRun.GetProperty("confirmationToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        var executeArguments = new Dictionary<string, object?>(arguments)
        {
            ["dryRun"] = false,
            ["confirmationToken"] = token,
        };

        var execute = await CallAsync(client, toolId, executeArguments);
        AssertStatus(execute, "Succeeded");
        return execute;
    }

    private static string? GetOptionalString(JsonElement value, string propertyName)
    {
        var property = value.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static void CopyOptionalString(
        JsonElement source,
        IDictionary<string, object?> destination,
        string propertyName)
    {
        var value = GetOptionalString(source, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            destination[propertyName] = value;
        }
    }

    private static async Task<JsonElement> GetUserVoiceConfigAsync(
        McpClient client,
        string tenantId,
        string credentialRef,
        string userUpn) =>
        await CallAsync(
            client,
            "get-user-voice-config",
            new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["credentialRef"] = credentialRef,
                ["userUpn"] = userUpn,
            });

    private static void AssertStatus(JsonElement envelope, string expected)
    {
        Assert.True(
            string.Equals(envelope.GetProperty("status").GetString(), expected, StringComparison.Ordinal),
            envelope.GetRawText());
    }

    private static void AssertAllChecksPassed(JsonElement envelope, string propertyName)
    {
        var checks = envelope.GetProperty(propertyName);
        Assert.Equal(JsonValueKind.Array, checks.ValueKind);
        foreach (var check in checks.EnumerateArray())
        {
            Assert.True(
                check.GetProperty("passed").GetBoolean(),
                $"{check.GetProperty("check").GetString()}: {check.GetProperty("detail").GetString()}");
        }
    }

    private static async Task<JsonElement> CallAsync(
        McpClient client,
        string toolId,
        Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(toolId, arguments);
        var envelopeJson = result.StructuredContent?.GetRawText() ?? "<no structured content>";
        Assert.False(result.IsError, $"{toolId} returned an error.\nEnvelope: {envelopeJson}");
        Assert.NotNull(result.StructuredContent);
        return result.StructuredContent!.Value;
    }

    private static async Task<McpClient> CreateClientAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = factory.CreateClient();
        httpClient.Timeout = TimeSpan.FromMinutes(15);
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
        if (!Directory.Exists(_auditRoot.Path))
        {
            return [];
        }

        return Directory.EnumerateFiles(_auditRoot.Path, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }
}