using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;
using TeamsPhoneMcp.Core.Sessions;

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
    public async Task AddTeamsPhoneTools_RegistersTenantSessionDefaults()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddMcpServer().AddTeamsPhoneTools();

        using var host = builder.Build();
        await host.StartAsync();

        var options = host.Services.GetRequiredService<IOptions<TenantSessionOptions>>().Value;
        var concreteManager = host.Services.GetRequiredService<TenantSessionManager>();
        Assert.Equal(TimeSpan.FromMinutes(10), options.IdleTimeout);
        Assert.Equal(10, options.MaxSessions);
        Assert.Equal(TimeSpan.FromMinutes(1), options.CleanupInterval);
        Assert.Same(concreteManager, host.Services.GetRequiredService<ITenantSessionManager>());

        await host.StopAsync();
    }

    [Fact]
    public async Task AddTeamsPhoneTools_DefaultTenantSessionFactoryFailsClosed()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddMcpServer().AddTeamsPhoneTools();

        using var host = builder.Build();
        await host.StartAsync();
        var manager = host.Services.GetRequiredService<ITenantSessionManager>();

        var exception = await Assert.ThrowsAsync<TenantSessionException>(() =>
            manager.ExecuteAsync(
                new TenantSessionContext(
                    Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    "test-credential"),
                TenantOperationKind.Read,
                static (_, _) => Task.FromResult(true)));

        Assert.Equal("tenantSessionFactoryUnavailable", exception.ErrorCode);
        await host.StopAsync();
    }

    [Fact]
    public void AddTeamsPhoneTools_PreservesRegisteredTenantSessionFactory()
    {
        var factory = new TestTenantSessionFactory();
        var services = new ServiceCollection();
        services.AddSingleton<ITenantSessionFactory>(factory);
        services.AddMcpServer().AddTeamsPhoneTools();

        using var provider = services.BuildServiceProvider();

        Assert.Same(factory, provider.GetRequiredService<ITenantSessionFactory>());
    }

    [Theory]
    [InlineData("TenantSessions:IdleTimeout", "00:00:00")]
    [InlineData("TenantSessions:MaxSessions", "0")]
    [InlineData("TenantSessions:CleanupInterval", "00:11:00")]
    public async Task AddTeamsPhoneTools_FailsHostStartup_WhenTenantSessionOptionsAreInvalid(
        string key,
        string value)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration[key] = value;
        builder.Services.AddMcpServer().AddTeamsPhoneTools();

        using var host = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
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

    [Fact]
    public async Task AddTeamsPhoneTools_RegistersPipelineRunnerWithFailClosedStageExecutor()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddMcpServer().AddTeamsPhoneTools();

        using var host = builder.Build();
        await host.StartAsync();

        Assert.NotNull(host.Services.GetRequiredService<IToolPipelineRunner>());
        var executor = host.Services.GetRequiredService<IStageExecutor>();
        var request = new StageExecutionRequest(
            new StubSession(),
            SampleManifest(),
            ToolStage.Execute,
            "{}",
            Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<TenantSessionException>(() =>
            executor.ExecuteAsync(request, CancellationToken.None));

        await host.StopAsync();
    }

    [Fact]
    public void AddTeamsPhoneTools_PreservesRegisteredStageExecutor()
    {
        var executor = new FakeStageExecutor();
        var services = new ServiceCollection();
        services.AddSingleton<IStageExecutor>(executor);
        services.AddMcpServer().AddTeamsPhoneTools();

        using var provider = services.BuildServiceProvider();
        Assert.Same(executor, provider.GetRequiredService<IStageExecutor>());
    }

    [Fact]
    public async Task AddPowerShellStageExecution_ReplacesDefaultWithRunspaceExecutor()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddMcpServer().AddTeamsPhoneTools().AddPowerShellStageExecution();

        using var host = builder.Build();
        await host.StartAsync();

        Assert.IsType<RunspaceStageExecutor>(host.Services.GetRequiredService<IStageExecutor>());
        Assert.NotNull(host.Services.GetRequiredService<ToolScriptLocator>());

        await host.StopAsync();
    }

    private static ToolManifest SampleManifest() => new()
    {
        Id = "sample-tool",
        Version = "1.0.0",
        Summary = "Sample.",
        Category = "read",
        RiskTier = 0,
        Annotations = new ToolManifestAnnotations(),
        Inputs = new Dictionary<string, ToolManifestInput>()
    };

    private sealed class StubSession : ITenantExecutionSession
    {
        public TenantSessionContext Context { get; } = new(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "test-credential");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class TestTenantSessionFactory : ITenantSessionFactory
    {
        public ValueTask<ITenantExecutionSession> CreateAsync(
            TenantSessionContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ITenantExecutionSession>(new NotSupportedException());
    }
}
