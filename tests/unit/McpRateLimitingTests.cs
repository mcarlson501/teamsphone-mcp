using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using TeamsPhoneMcp.Host;

namespace TeamsPhoneMcp.UnitTests;

public class McpRateLimitingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ValidToken = "test-token-abc123";
    private readonly WebApplicationFactory<Program> _factory;

    public McpRateLimitingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", ValidToken);
            builder.UseSetting("RateLimiting:PermitLimit", "2");
            builder.UseSetting("RateLimiting:Window", "00:01:00");
        });
    }

    [Fact]
    public async Task Mcp_WhenSessionLimitExceeded_Returns429WithRetryAfter()
    {
        using var client = CreateAuthorizedClient();

        var first = await SendMcpRequestAsync(client, "session-a");
        var second = await SendMcpRequestAsync(client, "session-a");
        var rejected = await SendMcpRequestAsync(client, "session-a");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    [Fact]
    public async Task Mcp_UsesIndependentLimitForEachSession()
    {
        using var client = CreateAuthorizedClient();

        await SendMcpRequestAsync(client, "session-a");
        await SendMcpRequestAsync(client, "session-a");
        var rejected = await SendMcpRequestAsync(client, "session-a");
        var otherSession = await SendMcpRequestAsync(client, "session-b");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherSession.StatusCode);
    }

    [Fact]
    public async Task Mcp_UnauthenticatedRequestsDoNotConsumeRateLimit()
    {
        using var unauthenticatedClient = _factory.CreateClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var response = await SendMcpRequestAsync(unauthenticatedClient, "session-a");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var authenticatedClient = CreateAuthorizedClient();
        var authenticatedResponse = await SendMcpRequestAsync(authenticatedClient, "session-a");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, authenticatedResponse.StatusCode);
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ValidToken);
        return client;
    }

    private static async Task<HttpResponseMessage> SendMcpRequestAsync(
        HttpClient client,
        string sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Add("Mcp-Session-Id", sessionId);
        return await client.SendAsync(request);
    }
}