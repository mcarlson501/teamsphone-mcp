using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using TeamsPhoneMcp.Host;
using Xunit.Abstractions;

namespace TeamsPhoneMcp.UnitTests;

/// <summary>
/// Gated end-to-end test for <c>get-user-voice-config</c> against a real dev
/// tenant. It runs only when the machine has a configured credential and the
/// three integration environment variables below are set; otherwise it skips
/// cleanly (passes as a no-op) so the default test run needs no tenant.
///
/// To run it, configure a <c>credentialRef</c> (see docs/setup-entra-app.md) and set:
///   TEAMSPHONE_MCP_IT_TENANT_ID      the tenant GUID
///   TEAMSPHONE_MCP_IT_CREDENTIAL_REF the configured credential reference name
///   TEAMSPHONE_MCP_IT_USER_UPN       a user UPN in that tenant
/// </summary>
public class GetUserVoiceConfigIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public GetUserVoiceConfigIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GetUserVoiceConfig_ReturnsConfiguration_WhenTenantConfigured()
    {
        var tenantId = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_TENANT_ID");
        var credentialRef = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_CREDENTIAL_REF");
        var userUpn = Environment.GetEnvironmentVariable("TEAMSPHONE_MCP_IT_USER_UPN");

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(credentialRef) ||
            string.IsNullOrWhiteSpace(userUpn))
        {
            // No dev-tenant credentials configured: skip cleanly.
            return;
        }

        const string token = "integration-token";
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", token));

        var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(
            "get-user-voice-config",
            new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["credentialRef"] = credentialRef,
                ["userUpn"] = userUpn
            });

        // Surface the full envelope so a failed live call is diagnosable from the test output.
        var envelopeJson = result.StructuredContent?.GetRawText() ?? "<no structured content>";
        var textContent = string.Join(
            "\n",
            result.Content
                .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                .Select(block => block.Text));

        Assert.False(
            result.IsError,
            $"Tool call returned an error.\nEnvelope: {envelopeJson}\nContent: {textContent}");
        Assert.NotNull(result.StructuredContent);

        var envelope = result.StructuredContent!.Value;
        Assert.Equal("Succeeded", envelope.GetProperty("status").GetString());

        var after = envelope.GetProperty("diff").GetProperty("after");
        _output.WriteLine("Voice configuration returned for " + userUpn + ":");
        _output.WriteLine(JsonSerializer.Serialize(after, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(
            userUpn,
            after.GetProperty("userPrincipalName").GetString());
    }
}
