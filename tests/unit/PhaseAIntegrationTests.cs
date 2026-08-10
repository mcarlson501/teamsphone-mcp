using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using TeamsPhoneMcp.Host;
using Xunit.Abstractions;

namespace TeamsPhoneMcp.UnitTests;

/// <summary>
/// Gated live-tenant verification of the M3 Phase A read tools. It calls every
/// Phase A tool once against a real dev tenant, asserts each result envelope, and
/// then asserts that the audit trail recorded every call with no secret material
/// (build spec M3: "snapshot of dev tenant completes; every call produces a valid
/// audit record").
///
/// The test skips cleanly (passes as a no-op) unless the tenant environment
/// variables below are set, so the default <c>dotnet test</c> run needs no tenant.
///
/// Required:
///   TEAMSPHONE_MCP_IT_TENANT_ID       the tenant GUID
///   TEAMSPHONE_MCP_IT_CREDENTIAL_REF  the configured credential reference name
///   TEAMSPHONE_MCP_IT_USER_UPN        a user UPN in that tenant
///
/// Optional (discovered from the tenant's resource accounts when unset; the tool is
/// skipped when the tenant has no such object):
///   TEAMSPHONE_MCP_IT_CALL_QUEUE      a call queue name or GUID
///   TEAMSPHONE_MCP_IT_AUTO_ATTENDANT  an auto attendant name or GUID
///
/// The credential itself is supplied the same way the host reads it — either from
/// <c>appsettings.Development.json</c> or from environment variables, for example:
///   Credentials__dev-tenant__TenantId, __ClientId, __CertificatePath,
///   __CertificatePasswordEnvVar (see docs/setup-entra-app.md).
/// </summary>
public sealed class PhaseAIntegrationTests : IDisposable
{
    private const string BearerToken = "phase-a-integration-token";

    private static readonly Regex[] SecretShapes =
    {
        new("-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.IgnoreCase),
        new("\\b[0-9a-f]{40}\\b", RegexOptions.IgnoreCase),
        new("\\beyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\.", RegexOptions.None),
    };

    private readonly ITestOutputHelper _output;
    private readonly TempAuditRoot _auditRoot = new();

    public PhaseAIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose() => _auditRoot.Dispose();

    [Fact]
    public async Task PhaseATools_SucceedAgainstDevTenant_AndAreFullyAudited()
    {
        var tenantId = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_TENANT_ID");
        var credentialRef = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_CREDENTIAL_REF");
        var userUpn = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_USER_UPN");
        var callQueueIdentity = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_CALL_QUEUE");
        var autoAttendantIdentity = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_AUTO_ATTENDANT");

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(credentialRef) ||
            string.IsNullOrWhiteSpace(userUpn))
        {
            // No dev-tenant credentials configured: skip cleanly.
            return;
        }

        await using var factory = new TestServerHost(builder =>
            {
                builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", BearerToken);
                builder.UseSetting("Audit:Enabled", "true");
                builder.UseSetting("Audit:RootPath", _auditRoot.Path);
            });

        await using var client = await CreateClientAsync(factory);

        var calls = new List<(string ToolId, Dictionary<string, object?> Arguments)>
        {
            ("get-user-voice-config", new() { ["userUpn"] = userUpn }),
            ("check-user-licensing", new() { ["userUpn"] = userUpn }),
            ("list-phone-numbers", new() { ["assignmentStatus"] = "all", ["pageSize"] = 25 }),
            ("list-voice-policies", new() { ["policyType"] = "all" }),
            ("list-resource-accounts", new() { ["pageSize"] = 25 }),
            ("list-emergency-addresses", new() { ["pageSize"] = 25 }),
            ("get-schedules", new() { ["pageSize"] = 25 }),
            ("get-tenant-voice-snapshot", new()),
        };

        var executedTools = new List<string>();
        var correlationIds = new List<string>();
        var envelopes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        async Task CallAndAssertAsync(string toolId, Dictionary<string, object?> arguments)
        {
            arguments["tenantId"] = tenantId;
            arguments["credentialRef"] = credentialRef;

            var result = await client.CallToolAsync(toolId, arguments);
            var envelopeJson = result.StructuredContent?.GetRawText() ?? "<no structured content>";

            Assert.False(result.IsError, $"{toolId} returned an error.\nEnvelope: {envelopeJson}");
            Assert.NotNull(result.StructuredContent);

            var envelope = result.StructuredContent!.Value;
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
            envelopes[toolId] = envelope;

            _output.WriteLine($"{toolId}: {envelope.GetProperty("summary").GetString()}");
        }

        foreach (var (toolId, arguments) in calls)
        {
            await CallAndAssertAsync(toolId, arguments);
        }

        // The two identity-scoped tools need a real object. Prefer an explicit
        // environment variable, otherwise discover one from the resource accounts
        // this tenant already returned.
        callQueueIdentity ??= FindAttachedConfiguration(envelopes["list-resource-accounts"], "CallQueue");
        autoAttendantIdentity ??= FindAttachedConfiguration(envelopes["list-resource-accounts"], "AutoAttendant");

        if (!string.IsNullOrWhiteSpace(callQueueIdentity))
        {
            await CallAndAssertAsync("get-callqueue-config", new() { ["callQueueIdentity"] = callQueueIdentity });
        }
        else
        {
            _output.WriteLine("get-callqueue-config: skipped — no call queue found in this tenant.");
        }

        if (!string.IsNullOrWhiteSpace(autoAttendantIdentity))
        {
            await CallAndAssertAsync(
                "get-autoattendant-config",
                new() { ["autoAttendantIdentity"] = autoAttendantIdentity });
        }
        else
        {
            _output.WriteLine("get-autoattendant-config: skipped — no auto attendant found in this tenant.");
        }

        // Every call — and only those calls — must appear in the audit trail.
        var records = ReadAuditRecords();
        Assert.Equal(executedTools.Count, records.Count);

        foreach (var toolId in executedTools)
        {
            var record = Assert.Single(records, r => r.GetProperty("toolId").GetString() == toolId);
            Assert.Equal(1, record.GetProperty("recordVersion").GetInt32());
            Assert.Equal("Succeeded", record.GetProperty("status").GetString());
            Assert.Equal(0, record.GetProperty("riskTier").GetInt32());
            Assert.False(record.GetProperty("dryRun").GetBoolean());
            Assert.Contains(record.GetProperty("correlationId").GetString(), correlationIds);
            Assert.False(record.TryGetProperty("errorCode", out _), $"{toolId} recorded an error code.");
        }

        // §10 log scrubber: no credential material may reach the audit store.
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

        _output.WriteLine($"{records.Count} audit records written under {_auditRoot.Path}.");
    }

    /// <summary>
    /// Pulls the id of a call queue or auto attendant that a resource account is
    /// attached to, so the identity-scoped tools can run without extra configuration.
    /// </summary>
    private static string? FindAttachedConfiguration(JsonElement resourceAccountEnvelope, string configurationType)
    {
        var accounts = resourceAccountEnvelope
            .GetProperty("diff")
            .GetProperty("after")
            .GetProperty("resourceAccounts");

        foreach (var account in accounts.EnumerateArray())
        {
            if (!account.TryGetProperty("association", out var association) ||
                association.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = association.TryGetProperty("configurationType", out var typeElement)
                ? typeElement.GetString()
                : null;

            if (!string.Equals(type, configurationType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = association.TryGetProperty("configurationId", out var idElement)
                ? idElement.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return null;
    }

    private static async Task<McpClient> CreateClientAsync(TestServerHost factory)
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
