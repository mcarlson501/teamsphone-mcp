using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core;
using TeamsPhoneMcp.Core.Manifests;
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
        Assert.All(tools, tool => Assert.IsType<ManifestValidatingMcpServerTool>(tool));
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

    [Fact]
    public async Task AddTeamsPhoneTools_ValidatesCopiedManifestsAtHostStartup()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddMcpServer().AddTeamsPhoneTools();

        using var host = builder.Build();
        await host.StartAsync();

        var catalog = host.Services.GetRequiredService<IToolManifestCatalog>();
        Assert.Contains(catalog.All, manifest => manifest.Id == "mock-write-user-policy");

        await host.StopAsync();
    }

    [Fact]
    public async Task AddTeamsPhoneTools_FailsHostStartup_WhenManifestDirectoryIsMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-tools-{Guid.NewGuid():N}");
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration["ToolManifests:ToolsRootPath"] = missingPath;
        builder.Services.AddMcpServer().AddTeamsPhoneTools();

        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("does not exist", exception.Message);
    }

    [Fact]
    public async Task AddTeamsPhoneTools_FailsHostStartup_WhenManifestAnnotationsDiffer()
    {
        var toolsRoot = CopyBuiltManifestCatalog();
        try
        {
            var pingManifestPath = Path.Combine(toolsRoot, "ping", "manifest.yaml");
            var manifestYaml = File.ReadAllText(pingManifestPath);
            File.WriteAllText(
                pingManifestPath,
                manifestYaml.Replace("idempotentHint: true", "idempotentHint: false", StringComparison.Ordinal));
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration["ToolManifests:ToolsRootPath"] = toolsRoot;
            builder.Services.AddMcpServer().AddTeamsPhoneTools();

            using var host = builder.Build();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

            Assert.Contains("annotations do not match", exception.Message);
        }
        finally
        {
            Directory.Delete(toolsRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AddTeamsPhoneTools_FailsHostStartup_WhenManifestInputSchemaDiffers()
    {
        var toolsRoot = CopyBuiltManifestCatalog();
        try
        {
            var pingManifestPath = Path.Combine(toolsRoot, "ping", "manifest.yaml");
            var manifestYaml = File.ReadAllText(pingManifestPath);
            File.WriteAllText(
                pingManifestPath,
                manifestYaml.Replace("message: {type: string", "message: {type: boolean", StringComparison.Ordinal));
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration["ToolManifests:ToolsRootPath"] = toolsRoot;
            builder.Services.AddMcpServer().AddTeamsPhoneTools();

            using var host = builder.Build();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

            Assert.Contains("input 'message' does not match", exception.Message);
        }
        finally
        {
            Directory.Delete(toolsRoot, recursive: true);
        }
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

    private static string CopyBuiltManifestCatalog()
    {
        var sourceRoot = Path.Combine(AppContext.BaseDirectory, "tools");
        var destinationRoot = Path.Combine(Path.GetTempPath(), $"teamsphone-tools-{Guid.NewGuid():N}");

        foreach (var sourcePath in Directory.GetFiles(sourceRoot, "manifest.yaml", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }

        return destinationRoot;
    }
}
