using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Sessions;
using TeamsPhoneMcp.Host;

namespace TeamsPhoneMcp.UnitTests;

public sealed class SessionWhatIfAcceptanceTests
{
    private const string BearerToken = "session-whatif-token";
    private const string ProtocolVersion = "2025-11-25";

    [Fact]
    public async Task SessionWhatIfMode_ForcesSimulationAndIssuesNoToken()
    {
        var executor = new FakeStageExecutor();
        await using var host = CreateServerHost(executor);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);

        var sessionId = await InitializeSessionAsync(client, whatIfMode: true);
        var envelope = await CallMoveAsync(client, sessionId);

        AssertSimulatedWithoutToken(envelope);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Preflight, ToolStage.DryRun], executor.InvokedStages);
    }

    [Fact]
    public async Task SessionWhatIfMode_ForcesSimulationForAttributedWriteTool()
    {
        await using var host = CreateServerHost(new FakeStageExecutor());
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);

        var sessionId = await InitializeSessionAsync(client, whatIfMode: true);
        var response = await PostAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = "mock-write-user-policy",
                    arguments = new Dictionary<string, object?>
                    {
                        ["tenantId"] = "11111111-1111-1111-1111-111111111111",
                        ["targetUserUpn"] = "target@contoso.com",
                        ["policyName"] = "Global",
                        ["dryRun"] = false,
                    },
                },
            },
            sessionId);

        response.EnsureSuccessStatusCode();
        var payload = await ReadJsonRpcPayloadAsync(response);
        Assert.False(payload.TryGetProperty("error", out _), payload.GetRawText());
        var resultText = payload
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString();
        using var resultDocument = JsonDocument.Parse(Assert.IsType<string>(resultText));
        var result = resultDocument.RootElement;
        Assert.Equal("dryRunCompleted", result.GetProperty("status").GetString());
        Assert.True(result.GetProperty("dryRun").GetBoolean());
        Assert.True(result.GetProperty("simulated").GetBoolean());
        Assert.True(
            !result.TryGetProperty("confirmationToken", out var token) ||
            token.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task ServerWhatIfMode_ForcesSimulationAndIssuesNoToken()
    {
        var executor = new FakeStageExecutor();
        await using var host = CreateServerHost(executor, serverMode: "whatif");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);

        var sessionId = await InitializeSessionAsync(client, whatIfMode: false);
        var envelope = await CallMoveAsync(client, sessionId);

        AssertSimulatedWithoutToken(envelope);
        Assert.Equal([ToolStage.Snapshot, ToolStage.Preflight, ToolStage.DryRun], executor.InvokedStages);
    }

    [Fact]
    public async Task InvalidSessionWhatIfMode_IsRejectedDuringInitialization()
    {
        await using var host = CreateServerHost(new FakeStageExecutor());
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);

        var response = await PostAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { },
                    clientInfo = new { name = "session-whatif-test", version = "1.0" },
                    _meta = new { whatIfMode = "yes" },
                },
            });

        response.EnsureSuccessStatusCode();
        var payload = await ReadJsonRpcPayloadAsync(response);
        Assert.True(payload.TryGetProperty("error", out var error));
        Assert.Contains("whatIfMode", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private static TestServerHost CreateServerHost(
        FakeStageExecutor executor,
        string? serverMode = null) =>
        new(builder =>
        {
            builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", BearerToken);
            if (serverMode is not null)
            {
                builder.UseSetting("TEAMSPHONE_MCP_MODE", serverMode);
            }
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStageExecutor>();
                services.AddSingleton<IStageExecutor>(executor);
                services.RemoveAll<ITenantSessionManager>();
                services.AddSingleton<ITenantSessionManager, InlineSessionManager>();
            });
        });

    private static async Task<JsonElement> CallMoveAsync(HttpClient client, string sessionId)
    {
        var response = await PostAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = "move-number-between-users",
                    arguments = new Dictionary<string, object?>
                    {
                        ["tenantId"] = "11111111-1111-1111-1111-111111111111",
                        ["credentialRef"] = "test-credential",
                        ["sourceUserUpn"] = "source@contoso.com",
                        ["targetUserUpn"] = "target@contoso.com",
                        ["dryRun"] = false,
                    },
                },
            },
            sessionId);

        response.EnsureSuccessStatusCode();
        var payload = await ReadJsonRpcPayloadAsync(response);
        return payload.GetProperty("result").GetProperty("structuredContent");
    }

    private static void AssertSimulatedWithoutToken(JsonElement envelope)
    {
        Assert.Equal("DryRunCompleted", envelope.GetProperty("status").GetString());
        Assert.True(envelope.GetProperty("dryRun").GetBoolean());
        Assert.True(envelope.GetProperty("simulated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("confirmationToken").ValueKind);
    }

    private static async Task<string> InitializeSessionAsync(HttpClient client, bool whatIfMode)
    {
        var response = await PostAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { },
                    clientInfo = new { name = "session-whatif-test", version = "1.0" },
                    _meta = new { whatIfMode },
                },
            });

        response.EnsureSuccessStatusCode();
        _ = await ReadJsonRpcPayloadAsync(response);
        var sessionId = Assert.Single(response.Headers.GetValues("Mcp-Session-Id"));

        var initialized = await PostAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized",
                @params = new { },
            },
            sessionId);
        initialized.EnsureSuccessStatusCode();

        return sessionId;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        object payload,
        string? sessionId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (sessionId is not null)
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
            request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonRpcPayloadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var payload = body.TrimStart().StartsWith('{')
            ? body
            : body
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .First(line => line.StartsWith("data:", StringComparison.Ordinal))
                ["data:".Length..]
                .Trim();

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private sealed class InlineSessionManager : ITenantSessionManager
    {
        public Task<TResult> ExecuteAsync<TResult>(
            TenantSessionContext context,
            TenantOperationKind operationKind,
            Func<ITenantExecutionSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default) =>
            operation(new InlineSession(context), cancellationToken);

        private sealed class InlineSession(TenantSessionContext context) : ITenantExecutionSession
        {
            public TenantSessionContext Context { get; } = context;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}