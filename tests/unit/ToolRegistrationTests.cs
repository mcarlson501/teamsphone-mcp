using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core;
using TeamsPhoneMcp.Core.Policy;

namespace TeamsPhoneMcp.UnitTests;

public class ToolRegistrationTests
{
    [Fact]
    public void AddTeamsPhoneTools_RegistersPingAndMockWriteTools()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().AddTeamsPhoneTools();

        using var provider = services.BuildServiceProvider();
        var tools = provider.GetServices<McpServerTool>().ToList();

        var names = tools.Select(t => t.ProtocolTool.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(["mock-write-user-policy", "ping"], names);
    }

    [Fact]
    public void PingTool_IsAnnotatedReadOnly()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().AddTeamsPhoneTools();

        using var provider = services.BuildServiceProvider();
        var tool = provider.GetServices<McpServerTool>().Single(t => t.ProtocolTool.Name == "ping");

        Assert.NotNull(tool.ProtocolTool.Annotations);
        Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
    }

    [Theory]
    [InlineData("not-valid-base64!!!")]
    [InlineData("dG9vc2hvcnQ=")]
    public void AddTeamsPhoneTools_ThrowsInvalidOperationException_WhenTokenKeyIsInvalid(string invalidKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY"] = invalidKey
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddMcpServer().AddTeamsPhoneTools();

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IConfirmationTokenService>());

        Assert.Contains("TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY", ex.Message);
        Assert.Contains("CreateRandomBase64Key", ex.Message);
    }
}
