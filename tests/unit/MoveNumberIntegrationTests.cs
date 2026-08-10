using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using TeamsPhoneMcp.Host;
using Xunit.Abstractions;

namespace TeamsPhoneMcp.UnitTests;

/// <summary>
/// Gated live-tenant demonstration of the M4 write pipeline: dry-run → confirmation
/// token → execute → verify, run against a real tenant through the MCP host, with
/// every call audited. The number is moved back to the source user afterwards so the
/// tenant is left exactly as it was found.
///
/// The test skips cleanly (passes as a no-op) unless the environment variables below
/// are set, so the default <c>dotnet test</c> run needs no tenant.
///
/// Required:
///   TEAMSPHONE_MCP_IT_TENANT_ID          the tenant GUID
///   TEAMSPHONE_MCP_IT_CREDENTIAL_REF     the configured credential reference name
///   TEAMSPHONE_MCP_IT_MOVE_SOURCE_UPN    a user who currently holds a phone number
///   TEAMSPHONE_MCP_IT_MOVE_TARGET_UPN    a voice-licensed user with no number assigned
///
/// Optional:
///   TEAMSPHONE_MCP_IT_MOVE_NUMBER        the E.164 number to move (defaults to the
///                                        number the source user currently holds)
/// </summary>
public sealed class MoveNumberIntegrationTests : IDisposable
{
    private const string ToolId = "move-number-between-users";
    private const string BearerToken = "move-number-integration-token";

    private static readonly Regex[] SecretShapes =
    {
        new("-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.IgnoreCase),
        new("\\b[0-9a-f]{40}\\b", RegexOptions.IgnoreCase),
        new("\\beyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\.", RegexOptions.None),
    };

    private readonly ITestOutputHelper _output;
    private readonly TempAuditRoot _auditRoot = new();

    public MoveNumberIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose() => _auditRoot.Dispose();

    [Fact]
    public async Task MoveNumber_DryRunConfirmExecuteVerify_AgainstDevTenant()
    {
        var tenantId = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_TENANT_ID");
        var credentialRef = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_CREDENTIAL_REF");
        var sourceUpn = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_MOVE_SOURCE_UPN");
        var targetUpn = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_MOVE_TARGET_UPN");
        var phoneNumber = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_MOVE_NUMBER");

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(credentialRef) ||
            string.IsNullOrWhiteSpace(sourceUpn) ||
            string.IsNullOrWhiteSpace(targetUpn))
        {
            // No dev-tenant configuration: skip cleanly.
            return;
        }

        await using var factory = new TestServerHost(builder =>
            {
                builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", BearerToken);
                builder.UseSetting("Audit:Enabled", "true");
                builder.UseSetting("Audit:RootPath", _auditRoot.Path);
            });

        await using var client = await CreateClientAsync(factory);

        Dictionary<string, object?> MoveArguments(string from, string to)
        {
            var arguments = new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["credentialRef"] = credentialRef,
                ["sourceUserUpn"] = from,
                ["targetUserUpn"] = to,
            };

            if (!string.IsNullOrWhiteSpace(phoneNumber))
            {
                arguments["phoneNumber"] = phoneNumber;
            }

            return arguments;
        }

        // 1. Dry run. No token is supplied, so §6.4 forces a simulation and hands
        //    back the confirmation token that authorises the real change.
        var dryRun = await CallAsync(client, MoveArguments(sourceUpn, targetUpn));
        Assert.Equal("DryRunCompleted", dryRun.GetProperty("status").GetString());
        Assert.True(dryRun.GetProperty("dryRun").GetBoolean());
        AssertAllChecksPassed(dryRun, "preflight");

        var token = dryRun.GetProperty("confirmationToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token), "The dry run issued no confirmation token.");
        _output.WriteLine($"dry-run: {dryRun.GetProperty("summary").GetString()}");

        // 2. Confirmed execute. Recorded before the assertions so the number is moved
        //    back even if verification of the forward move fails.
        var moved = false;
        try
        {
            var executeArguments = MoveArguments(sourceUpn, targetUpn);
            executeArguments["dryRun"] = false;
            executeArguments["confirmationToken"] = token;

            var execute = await CallAsync(client, executeArguments);
            moved = string.Equals(execute.GetProperty("status").GetString(), "Succeeded", StringComparison.Ordinal);
            _output.WriteLine($"execute: {execute.GetProperty("summary").GetString()}");

            Assert.Equal("Succeeded", execute.GetProperty("status").GetString());
            Assert.False(execute.GetProperty("dryRun").GetBoolean());

            // 3. Verification ran on the tenant and agreed the move landed.
            AssertAllChecksPassed(execute, "verification");

            var diff = execute.GetProperty("diff");
            Assert.NotEqual(JsonValueKind.Null, diff.GetProperty("before").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, diff.GetProperty("after").ValueKind);
        }
        finally
        {
            if (moved)
            {
                await MoveBackAsync(client, MoveArguments(targetUpn, sourceUpn));
            }
        }

        // 4. Every call is in the audit trail, with no credential material.
        var records = ReadAuditRecords();
        Assert.Equal(4, records.Count);
        Assert.All(records, record => Assert.Equal(ToolId, record.GetProperty("toolId").GetString()));
        Assert.All(records, record => Assert.Equal(2, record.GetProperty("riskTier").GetInt32()));
        Assert.Equal(2, records.Count(record => record.GetProperty("dryRun").GetBoolean()));

        foreach (var record in records.Where(r => !r.GetProperty("dryRun").GetBoolean()))
        {
            Assert.Equal("Succeeded", record.GetProperty("status").GetString());
            AssertBothSnapshotsStored(record);
        }

        var auditText = string.Join("\n", records.Select(record => record.GetRawText()));
        Assert.DoesNotContain(credentialRef, auditText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token!, auditText, StringComparison.Ordinal);

        foreach (var shape in SecretShapes)
        {
            Assert.False(shape.IsMatch(auditText), $"Audit trail matched a secret shape: {shape}.");
        }

        _output.WriteLine($"{records.Count} audit records written under {_auditRoot.Path}.");
    }

    /// <summary>
    /// Proves the fail-closed half of the protocol on the live tenant: a target the
    /// tenant cannot accept is rejected by preflight, nothing is written, and no
    /// confirmation token is issued — so the change can never be promoted.
    ///
    /// Set TEAMSPHONE_MCP_IT_MOVE_INELIGIBLE_TARGET_UPN to a resource account or a
    /// user without the licence the number requires.
    /// </summary>
    [Fact]
    public async Task MoveNumber_PreflightBlocksAnIneligibleTarget_AndIssuesNoToken()
    {
        var tenantId = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_TENANT_ID");
        var credentialRef = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_CREDENTIAL_REF");
        var sourceUpn = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_MOVE_SOURCE_UPN");
        var targetUpn = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_MOVE_INELIGIBLE_TARGET_UPN");

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(credentialRef) ||
            string.IsNullOrWhiteSpace(sourceUpn) ||
            string.IsNullOrWhiteSpace(targetUpn))
        {
            return;
        }

        await using var factory = new TestServerHost(builder =>
            {
                builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", BearerToken);
                builder.UseSetting("Audit:Enabled", "true");
                builder.UseSetting("Audit:RootPath", _auditRoot.Path);
            });

        await using var client = await CreateClientAsync(factory);

        var envelope = await CallAsync(client, new Dictionary<string, object?>
        {
            ["tenantId"] = tenantId,
            ["credentialRef"] = credentialRef,
            ["sourceUserUpn"] = sourceUpn,
            ["targetUserUpn"] = targetUpn,
        });

        Assert.Equal("PreflightFailed", envelope.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("confirmationToken").ValueKind);
        Assert.Equal("preflightFailed", envelope.GetProperty("error").GetProperty("code").GetString());

        var failed = envelope.GetProperty("preflight").EnumerateArray()
            .Where(check => !check.GetProperty("passed").GetBoolean())
            .ToList();

        Assert.NotEmpty(failed);
        foreach (var check in failed)
        {
            _output.WriteLine($"blocked by: {check.GetProperty("check").GetString()} — {check.GetProperty("detail").GetString()}");
        }

        var record = Assert.Single(ReadAuditRecords());
        Assert.Equal("PreflightFailed", record.GetProperty("status").GetString());
        Assert.True(record.GetProperty("dryRun").GetBoolean());
    }

    /// <summary>Returns the tenant to its original state by moving the number back.</summary>
    private async Task MoveBackAsync(McpClient client, Dictionary<string, object?> arguments)
    {
        var dryRun = await CallAsync(client, arguments);
        var token = dryRun.GetProperty("confirmationToken").GetString();

        var executeArguments = new Dictionary<string, object?>(arguments)
        {
            ["dryRun"] = false,
            ["confirmationToken"] = token,
        };

        var execute = await CallAsync(client, executeArguments);
        _output.WriteLine($"restore: {execute.GetProperty("summary").GetString()}");
        Assert.Equal("Succeeded", execute.GetProperty("status").GetString());
    }

    private async Task<JsonElement> CallAsync(McpClient client, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(ToolId, arguments);
        var envelopeJson = result.StructuredContent?.GetRawText() ?? "<no structured content>";

        Assert.False(result.IsError, $"{ToolId} returned an error.\nEnvelope: {envelopeJson}");
        Assert.NotNull(result.StructuredContent);

        var envelope = result.StructuredContent!.Value;

        // A non-success status carries the tenant's own failure detail; print it so a
        // live run is diagnosable without re-running under a debugger.
        if (envelope.GetProperty("status").GetString() is not ("Succeeded" or "DryRunCompleted"))
        {
            _output.WriteLine($"envelope: {envelopeJson}");
        }

        return envelope;
    }

    private static void AssertAllChecksPassed(JsonElement envelope, string propertyName)
    {
        Assert.True(
            envelope.TryGetProperty(propertyName, out var checks) && checks.ValueKind == JsonValueKind.Array,
            $"The envelope carried no {propertyName} checks.");

        foreach (var check in checks.EnumerateArray())
        {
            Assert.True(
                check.GetProperty("passed").GetBoolean(),
                $"{propertyName} check failed: {check.GetProperty("check").GetString()} — {check.GetProperty("detail").GetString()}");
        }
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
