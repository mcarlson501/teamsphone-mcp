using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core;

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
}
