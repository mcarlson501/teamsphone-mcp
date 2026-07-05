using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using TeamsPhoneMcp.Host;
using TeamsPhoneMcp.Host.Auth;

namespace TeamsPhoneMcp.UnitTests;

public class BearerAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ValidToken = "test-token-abc123";
    private readonly WebApplicationFactory<Program> _factory;

    public BearerAuthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", ValidToken));
    }

    [Fact]
    public async Task Mcp_WithoutToken_Returns401AndNoBody()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/mcp");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Empty(body); // never leak a tool listing or protocol payload
    }

    [Fact]
    public async Task Mcp_WithWrongToken_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        var response = await client.GetAsync("/mcp");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_WithValidToken_PassesAuth()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken);

        var response = await client.GetAsync("/mcp");

        // Auth passed if we get past the middleware; the MCP handler may itself
        // reject a bare GET, but it must NOT be a 401 from our auth layer.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NonMcpPath_IsNotBlockedByAuth()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_WithNoTokenConfigured_FailsClosed()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", string.Empty));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken);

        var response = await client.GetAsync("/mcp");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_WithOversizedToken_Returns401()
    {
        var client = _factory.CreateClient();
        var oversized = new string('a', BearerAuthMiddleware.MaxTokenLength + 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oversized);

        var response = await client.GetAsync("/mcp");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_WithTokenAtExactMaxLength_IsEvaluatedNormally()
    {
        // A token exactly at the limit that does not match the configured token
        // must still yield 401, not be silently rejected by the length guard.
        var client = _factory.CreateClient();
        var exactMax = new string('a', BearerAuthMiddleware.MaxTokenLength);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", exactMax);

        var response = await client.GetAsync("/mcp");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
