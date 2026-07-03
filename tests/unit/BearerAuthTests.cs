using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using TeamsPhoneMcp.Host;

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
}
