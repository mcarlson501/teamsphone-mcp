using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core;

namespace TeamsPhoneMcp.UnitTests;

public class ToolRegistrationTests
{
    [Fact]
    public void AddTeamsPhoneTools_RegistersExactlyThePingTool()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().AddTeamsPhoneTools();

        using var provider = services.BuildServiceProvider();
        var tools = provider.GetServices<McpServerTool>().ToList();

        var tool = Assert.Single(tools);
        Assert.Equal("ping", tool.ProtocolTool.Name);
    }

    [Fact]
    public void PingTool_IsAnnotatedReadOnly()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().AddTeamsPhoneTools();

        using var provider = services.BuildServiceProvider();
        var tool = provider.GetServices<McpServerTool>().Single();

        Assert.NotNull(tool.ProtocolTool.Annotations);
        Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
    }
}
