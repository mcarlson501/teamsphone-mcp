using Microsoft.AspNetCore.Mvc.Testing;
using TeamsPhoneMcp.Host;
using TeamsPhoneMcp.Host.Logging;

namespace TeamsPhoneMcp.UnitTests;

public class CorrelationLoggingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CorrelationLoggingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Response_AlwaysCarriesCorrelationId()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.True(response.Headers.Contains(CorrelationLoggingMiddleware.CorrelationHeader));
    }

    [Fact]
    public async Task SafeInboundCorrelationId_IsEchoedBack()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationLoggingMiddleware.CorrelationHeader, "abc-123");

        var response = await client.GetAsync("/");

        var echoed = Assert.Single(response.Headers.GetValues(CorrelationLoggingMiddleware.CorrelationHeader));
        Assert.Equal("abc-123", echoed);
    }

    [Fact]
    public async Task UnsafeInboundCorrelationId_IsReplaced()
    {
        var client = _factory.CreateClient();
        // Spaces are valid to transmit but fail the safe-token pattern, so the
        // middleware must discard the inbound value and generate a fresh id.
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            CorrelationLoggingMiddleware.CorrelationHeader,
            "injected value with spaces");

        var response = await client.GetAsync("/");

        var echoed = Assert.Single(response.Headers.GetValues(CorrelationLoggingMiddleware.CorrelationHeader));
        Assert.DoesNotContain(' ', echoed);
        Assert.NotEqual("injected value with spaces", echoed);
    }
}
