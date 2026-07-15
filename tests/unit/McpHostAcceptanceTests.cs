using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TeamsPhoneMcp.Host;

namespace TeamsPhoneMcp.UnitTests;

public class McpHostAcceptanceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ValidToken = "test-token-abc123";
    private readonly WebApplicationFactory<Program> _factory;

    public McpHostAcceptanceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", ValidToken));
    }

    [Fact]
    public async Task ListTools_ExposesManifestParityContracts()
    {
        await using var client = await CreateMcpClientAsync();

        var tools = await client.ListToolsAsync();

        Assert.Equal(
            ["mock-write-user-policy", "ping"],
            tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal));

        var ping = tools.Single(tool => tool.Name == "ping").ProtocolTool;
        Assert.True(ping.Annotations?.ReadOnlyHint);
        Assert.False(ping.Annotations?.DestructiveHint);
        Assert.True(ping.Annotations?.IdempotentHint);
        Assert.True(ping.InputSchema.GetProperty("properties").TryGetProperty("message", out _));

        var mockWrite = tools.Single(tool => tool.Name == "mock-write-user-policy").ProtocolTool;
        Assert.False(mockWrite.Annotations?.ReadOnlyHint);
        Assert.False(mockWrite.Annotations?.DestructiveHint);
        Assert.True(mockWrite.Annotations?.IdempotentHint);
        var properties = mockWrite.InputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("targetUserUpn", out _));
        Assert.True(properties.TryGetProperty("confirmationToken", out _));
    }

    [Fact]
    public async Task CallTool_RejectsManifestInvalidArgumentsBeforeBinding()
    {
        await using var client = await CreateMcpClientAsync();

        var result = await client.CallToolAsync(
            "mock-write-user-policy",
            new Dictionary<string, object?>
            {
                ["tenantId"] = "00000000-0000-0000-0000-000000000001",
                ["targetUserUpn"] = "not-a-upn",
                ["policyName"] = "TestPolicy"
            });

        Assert.True(result.IsError);
        var errorContent = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("argument 'targetUserUpn' must be a valid UPN", errorContent.Text);
        Assert.DoesNotContain("not-a-upn", errorContent.Text);
    }

    [Fact]
    public async Task CallTool_InvokesValidRegisteredTool()
    {
        await using var client = await CreateMcpClientAsync();

        var result = await client.CallToolAsync(
            "ping",
            new Dictionary<string, object?> { ["message"] = "acceptance-pong" });

        Assert.NotEqual(true, result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("acceptance-pong", content.Text);
    }

    [Fact]
    public async Task CallTool_CompletesDryRunAndConfirmedExecution()
    {
        await using var client = await CreateMcpClientAsync();
        var baseArguments = new Dictionary<string, object?>
        {
            ["tenantId"] = "00000000-0000-0000-0000-000000000001",
            ["targetUserUpn"] = "user@example.com",
            ["policyName"] = "TestPolicy",
            ["blastRadius"] = 1
        };

        var dryRunResult = await client.CallToolAsync("mock-write-user-policy", baseArguments);
        var dryRun = ParseToolResult(dryRunResult);
        Assert.Equal("dryRunCompleted", dryRun.GetProperty("status").GetString());
        var confirmationToken = dryRun.GetProperty("confirmationToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(confirmationToken));

        var executeArguments = new Dictionary<string, object?>(baseArguments)
        {
            ["dryRun"] = false,
            ["confirmationToken"] = confirmationToken
        };
        var executeResult = await client.CallToolAsync("mock-write-user-policy", executeArguments);
        var execute = ParseToolResult(executeResult);

        Assert.Equal("succeeded", execute.GetProperty("status").GetString());
        Assert.False(execute.GetProperty("dryRun").GetBoolean());
        Assert.True(
            !execute.TryGetProperty("errorCode", out var errorCode) ||
            errorCode.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task CallTool_RejectsChangedParametersAfterDryRun()
    {
        await using var client = await CreateMcpClientAsync();
        var dryRunArguments = new Dictionary<string, object?>
        {
            ["tenantId"] = "00000000-0000-0000-0000-000000000001",
            ["targetUserUpn"] = "user@example.com",
            ["policyName"] = "OriginalPolicy"
        };
        var dryRunResult = await client.CallToolAsync("mock-write-user-policy", dryRunArguments);
        var confirmationToken = ParseToolResult(dryRunResult)
            .GetProperty("confirmationToken")
            .GetString();
        var changedArguments = new Dictionary<string, object?>(dryRunArguments)
        {
            ["policyName"] = "ChangedPolicy",
            ["dryRun"] = false,
            ["confirmationToken"] = confirmationToken
        };

        var changedResult = await client.CallToolAsync("mock-write-user-policy", changedArguments);
        var rejection = ParseToolResult(changedResult);

        Assert.Equal("policyRejected", rejection.GetProperty("status").GetString());
        Assert.Equal("invalidConfirmationToken", rejection.GetProperty("errorCode").GetString());
    }

    private async Task<McpClient> CreateMcpClientAsync()
    {
        var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ValidToken);
        var endpoint = new Uri(httpClient.BaseAddress!, "/mcp");
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport);
    }

    private static JsonElement ParseToolResult(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var document = JsonDocument.Parse(content.Text);
        return document.RootElement.Clone();
    }
}