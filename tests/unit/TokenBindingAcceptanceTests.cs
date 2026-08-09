using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeamsPhoneMcp.Audit;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Sessions;
using TeamsPhoneMcp.Host;
using TeamsPhoneMcp.Host.Auth;

namespace TeamsPhoneMcp.UnitTests;

/// <summary>
/// End-to-end coverage for build spec §15 S2: a replayed token is rejected, a token redeemed
/// in a different session is rejected, and audit records attribute two callers distinctly.
/// </summary>
public sealed class TokenBindingAcceptanceTests
{
    private const string ProtocolVersion = "2025-11-25";
    private const string AlphaToken = "alpha-client-token-0123456789";
    private const string BetaToken = "beta-client-token-9876543210";
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task ARedeemedConfirmationTokenCannotBeReplayed()
    {
        await using var factory = CreateFactory(out var sink);
        using var client = CreateClient(factory, AlphaToken);
        var sessionId = await InitializeSessionAsync(client);

        var dryRun = await CallMockWriteAsync(client, sessionId, dryRun: true);
        var token = dryRun.GetProperty("confirmationToken").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        var executed = await CallMockWriteAsync(client, sessionId, dryRun: false, confirmationToken: token);
        var replayed = await CallMockWriteAsync(client, sessionId, dryRun: false, confirmationToken: token);

        Assert.Equal("succeeded", executed.GetProperty("status").GetString());
        Assert.Equal("policyRejected", replayed.GetProperty("status").GetString());
        Assert.Equal("replayedConfirmationToken", replayed.GetProperty("errorCode").GetString());

        var replayRecord = sink.Records[^1];
        Assert.Equal("replayedConfirmationToken", replayRecord.ErrorCode);
    }

    [Fact]
    public async Task AConfirmationTokenCannotBeRedeemedInAnotherSession()
    {
        await using var factory = CreateFactory(out _);
        using var client = CreateClient(factory, AlphaToken);
        var issuingSession = await InitializeSessionAsync(client);
        var otherSession = await InitializeSessionAsync(client);

        var dryRun = await CallMockWriteAsync(client, issuingSession, dryRun: true);
        var token = dryRun.GetProperty("confirmationToken").GetString();

        var redeemed = await CallMockWriteAsync(client, otherSession, dryRun: false, confirmationToken: token);

        Assert.Equal("policyRejected", redeemed.GetProperty("status").GetString());
        Assert.Equal("sessionBoundConfirmationToken", redeemed.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task AuditRecordsAttributeTwoClientsDistinctly()
    {
        await using var factory = CreateFactory(out var sink);

        using var alpha = CreateClient(factory, AlphaToken);
        var alphaSession = await InitializeSessionAsync(alpha);
        await CallMockWriteAsync(alpha, alphaSession, dryRun: true);

        using var beta = CreateClient(factory, BetaToken);
        var betaSession = await InitializeSessionAsync(beta);
        await CallMockWriteAsync(beta, betaSession, dryRun: true);

        Assert.Equal(2, sink.Records.Count);
        Assert.Equal("alpha", sink.Records[0].ClientId);
        Assert.Equal("beta", sink.Records[1].ClientId);
    }

    [Theory]
    [InlineData(AlphaToken)]
    [InlineData(BetaToken)]
    public async Task EitherClientTokenIsAcceptedSoRotationNeedsNoDowntime(string token)
    {
        await using var factory = CreateFactory(out _);
        using var client = CreateClient(factory, token);

        using var response = await PostAsync(client, InitializePayload());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ASessionOpenedByOneClientCannotBeUsedByAnother()
    {
        await using var factory = CreateFactory(out _);
        using var alpha = CreateClient(factory, AlphaToken);
        var alphaSession = await InitializeSessionAsync(alpha);

        using var beta = CreateClient(factory, BetaToken);
        using var response = await PostAsync(beta, ToolsListPayload(), alphaSession);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A repeated session header would join into one value that no other component resolves,
    /// leaving the real session unowned, so it is refused outright.
    /// </summary>
    [Fact]
    public async Task ARepeatedSessionHeaderIsRejectedRatherThanTreatedAsAbsent()
    {
        await using var factory = CreateFactory(out _);
        using var alpha = CreateClient(factory, AlphaToken);
        var alphaSession = await InitializeSessionAsync(alpha);

        using var beta = CreateClient(factory, BetaToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(ToolsListPayload()),
        };
        request.Headers.Add("Mcp-Session-Id", new[] { alphaSession, "another-session" });
        request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);

        using var response = await beta.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The header parser trims, so a whitespace-only configured token could never match any
    /// request. Resolving it away makes the host report itself unconfigured and fail closed
    /// loudly, instead of looking configured while rejecting everything.
    /// </summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("")]
    public void AWhitespaceOnlyTokenIsResolvedAwayRatherThanLookingConfigured(string token)
    {
        var options = new BearerAuthOptions { BearerToken = token };
        options.ClientTokens["padded-out"] = token;

        Assert.Empty(options.ResolveClientTokens());
    }

    [Fact]
    public async Task SurroundingWhitespaceOnAConfiguredTokenIsIgnored()
    {
        await using var factory = CreateFactory(
            out _,
            builder => builder.UseSetting("Auth:ClientTokens:padded", $"  {AlphaToken}  "),
            configureNamedTokens: false);
        using var client = CreateClient(factory, AlphaToken);

        using var response = await PostAsync(client, InitializePayload());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheSingleTokenFormStillWorksAndAttributesAsTheDefaultClient()
    {
        await using var factory = CreateFactory(
            out var sink,
            builder => builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", AlphaToken),
            configureNamedTokens: false);
        using var client = CreateClient(factory, AlphaToken);
        var sessionId = await InitializeSessionAsync(client);

        await CallMockWriteAsync(client, sessionId, dryRun: true);

        Assert.Equal("default", Assert.Single(sink.Records).ClientId);
    }

    [Fact]
    public async Task StartupFailsWhenTwoClientsShareOneToken()
    {
        await using var factory = CreateFactory(
            out _,
            builder =>
            {
                builder.UseSetting("Auth:ClientTokens:alpha", AlphaToken);
                builder.UseSetting("Auth:ClientTokens:beta", AlphaToken);
            },
            configureNamedTokens: false);

        var failure = await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(
            async () =>
            {
                using var client = factory.CreateClient();
                await client.GetAsync("/");
            });

        Assert.Contains("share the same", failure.Message, StringComparison.Ordinal);
    }

    private static HttpClient CreateClient(TestServerHost host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static TestServerHost CreateFactory(
        out RecordingAuditSink sink,
        Action<IWebHostBuilder>? configure = null,
        bool configureNamedTokens = true)
    {
        var recordingSink = new RecordingAuditSink();
        sink = recordingSink;

        var seed = new WebApplicationFactory<Program>();
        var configured = seed.WithWebHostBuilder(builder =>
        {
            if (configureNamedTokens)
            {
                builder.UseSetting("Auth:ClientTokens:alpha", AlphaToken);
                builder.UseSetting("Auth:ClientTokens:beta", BetaToken);
            }

            configure?.Invoke(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStageExecutor>();
                services.AddSingleton<IStageExecutor>(new FakeStageExecutor());
                services.RemoveAll<ITenantSessionManager>();
                services.AddSingleton<ITenantSessionManager, InlineSessionManager>();
                services.RemoveAll<IAuditSink>();
                services.AddSingleton<IAuditSink>(recordingSink);
            });
        });

        return new TestServerHost(seed, configured);
    }

    /// <summary>
    /// Owns both factories: <c>WithWebHostBuilder</c> returns a second instance and does not
    /// dispose the one it was called on.
    /// </summary>
    private sealed class TestServerHost(
        WebApplicationFactory<Program> seed,
        WebApplicationFactory<Program> configured) : IAsyncDisposable
    {
        public HttpClient CreateClient() => configured.CreateClient();

        public async ValueTask DisposeAsync()
        {
            await configured.DisposeAsync();
            await seed.DisposeAsync();
        }
    }

    private static async Task<JsonElement> CallMockWriteAsync(
        HttpClient client,
        string sessionId,
        bool dryRun,
        string? confirmationToken = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["tenantId"] = TenantId,
            ["targetUserUpn"] = "target@contoso.com",
            ["policyName"] = "Global",
            ["dryRun"] = dryRun,
        };
        if (confirmationToken is not null)
        {
            arguments["confirmationToken"] = confirmationToken;
        }

        var response = await PostAsync(
            client,
            new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new { name = "mock-write-user-policy", arguments },
            },
            sessionId);

        response.EnsureSuccessStatusCode();
        var payload = await ReadJsonRpcPayloadAsync(response);
        Assert.False(payload.TryGetProperty("error", out _), payload.GetRawText());
        var text = payload.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var document = JsonDocument.Parse(Assert.IsType<string>(text));
        return document.RootElement.Clone();
    }

    private static async Task<string> InitializeSessionAsync(HttpClient client)
    {
        var response = await PostAsync(client, InitializePayload());
        response.EnsureSuccessStatusCode();
        _ = await ReadJsonRpcPayloadAsync(response);
        var sessionId = Assert.Single(response.Headers.GetValues("Mcp-Session-Id"));

        var initialized = await PostAsync(
            client,
            new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } },
            sessionId);
        initialized.EnsureSuccessStatusCode();

        return sessionId;
    }

    private static object InitializePayload() => new
    {
        jsonrpc = "2.0",
        id = 1,
        method = "initialize",
        @params = new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "token-binding-test", version = "1.0" },
        },
    };

    private static object ToolsListPayload() => new
    {
        jsonrpc = "2.0",
        id = 3,
        method = "tools/list",
        @params = new { },
    };

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

    private sealed class RecordingAuditSink : IAuditSink
    {
        private readonly List<AuditRecord> _records = [];

        public IReadOnlyList<AuditRecord> Records
        {
            get
            {
                lock (_records)
                {
                    return _records.ToList();
                }
            }
        }

        public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            lock (_records)
            {
                _records.Add(record);
            }

            return ValueTask.CompletedTask;
        }
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
