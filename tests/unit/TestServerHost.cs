using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TeamsPhoneMcp.Host;

namespace TeamsPhoneMcp.UnitTests;

internal sealed class TestServerHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _seed = new();
    private readonly WebApplicationFactory<Program> _configured;

    public TestServerHost(Action<IWebHostBuilder> configure) =>
        _configured = _seed.WithWebHostBuilder(configure);

    public HttpClient CreateClient() => _configured.CreateClient();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _configured.DisposeAsync();
        }
        finally
        {
            await _seed.DisposeAsync();
        }
    }
}
