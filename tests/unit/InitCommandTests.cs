using System.Net;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TeamsPhoneMcp.Host;

namespace TeamsPhoneMcp.UnitTests;

public sealed class InitCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"teamsphone-init-{Guid.NewGuid():N}");

    public InitCommandTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "docker-compose.yml"), "services: {}\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Prepare_GeneratesCertificateAndComposeConfigurationWithoutPrintingSecrets()
    {
        var output = new StringWriter();
        var options = new InitPrepareOptions(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "default",
            _root,
            ".env",
            Path.Combine(_root, "credentials"),
            Force: false);

        var prepared = await InitCommand.PrepareAsync(options, output);

        Assert.True(File.Exists(prepared.PublicCertificatePath));
        Assert.True(File.Exists(prepared.PrivateCertificatePath));
        Assert.True(File.Exists(prepared.EnvironmentFilePath));

        var environment = await File.ReadAllTextAsync(prepared.EnvironmentFilePath);
        var pfxPassword = ReadEnvironmentValue(environment, "TEAMSPHONE_MCP_CERTIFICATE_PASSWORD");
        var bearerToken = ReadEnvironmentValue(environment, "TEAMSPHONE_MCP_BEARER_TOKEN");
        var signingKey = ReadEnvironmentValue(environment, "TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY");

        var keyStorageFlags = OperatingSystem.IsMacOS()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;
        using var certificate = new X509Certificate2(
            prepared.PrivateCertificatePath,
            pfxPassword,
            keyStorageFlags);
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal("CN=teamsphone-mcp-default", certificate.Subject);
        var keyUsage = Assert.Single(certificate.Extensions.OfType<X509KeyUsageExtension>());
        Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));
        Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment));
        var enhancedKeyUsage = Assert.Single(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Contains(
            enhancedKeyUsage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(),
            usage => usage.Value == "1.3.6.1.5.5.7.3.2");
        Assert.InRange(
            certificate.NotAfter.ToUniversalTime(),
            DateTime.UtcNow.AddDays(364),
            DateTime.UtcNow.AddDays(366));

        Assert.Contains("TEAMSPHONE_MCP_MODE=whatif", environment, StringComparison.Ordinal);
        Assert.Contains($"TEAMSPHONE_MCP_TENANT_ID={options.TenantId:D}", environment, StringComparison.Ordinal);
        Assert.Contains($"TEAMSPHONE_MCP_CLIENT_ID={options.ClientId:D}", environment, StringComparison.Ordinal);
        Assert.Contains(prepared.PrivateCertificatePath, environment, StringComparison.Ordinal);

        var visibleOutput = output.ToString();
        Assert.Contains(prepared.PublicCertificatePath, visibleOutput, StringComparison.Ordinal);
        Assert.Contains(prepared.Thumbprint, visibleOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(pfxPassword, visibleOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(bearerToken, visibleOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(signingKey, visibleOutput, StringComparison.Ordinal);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(prepared.PrivateCertificatePath));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(prepared.EnvironmentFilePath));
        }
    }

    [Theory]
    [InlineData(new[] { "init" }, true)]
    [InlineData(new[] { "INIT", "prepare" }, true)]
    [InlineData(new[] { "--stdio" }, false)]
    [InlineData(new string[0], false)]
    public void Program_RoutesOnlyTheInitSubcommand(string[] arguments, bool expected)
    {
        Assert.Equal(expected, Program.IsInitCommand(arguments));
    }

    [Fact]
    public async Task Prepare_RefusesToOverwriteGeneratedFilesWithoutForce()
    {
        var options = new InitPrepareOptions(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "default",
            _root,
            ".env",
            Path.Combine(_root, "credentials"),
            Force: false);
        await InitCommand.PrepareAsync(options, TextWriter.Null);

        var exception = await Assert.ThrowsAsync<IOException>(
            () => InitCommand.PrepareAsync(options, TextWriter.Null));

        Assert.Contains("--force", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_StartsComposeAndProvesTenantToolConnectivity()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await File.WriteAllTextAsync(
            Path.Combine(_root, ".env"),
            $"""
            TEAMSPHONE_MCP_BEARER_TOKEN=test-bearer-token
            TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY=test-signing-key
            TEAMSPHONE_MCP_MODE=whatif
            TEAMSPHONE_MCP_PORT=5199
            TEAMSPHONE_MCP_TENANT_ID={tenantId:D}
            """);
        var responses = new Queue<HttpResponseMessage>(
        [
            new(HttpStatusCode.Unauthorized),
            JsonResponse(
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    result = new
                    {
                        protocolVersion = "2025-11-25",
                        serverInfo = new { name = "TeamsPhoneMcp.Host", version = "0.1.0" },
                        capabilities = new { },
                    },
                },
                sessionId: "session-1"),
            new(HttpStatusCode.Accepted),
            JsonResponse(
                new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    result = new
                    {
                        structuredContent = new
                        {
                            status = "Succeeded",
                            correlationId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                        },
                    },
                }),
        ]);
        var handler = new QueuedResponseHandler(responses);
        using var httpClient = new HttpClient(handler);
        var runner = new RecordingProcessRunner();
        var output = new StringWriter();
        var options = new InitVerifyOptions(
            _root,
            ".env",
            "demo.user@example.com",
            StartServer: true,
            TimeSpan.FromSeconds(1));

        var result = await InitCommand.VerifyAsync(options, output, runner, httpClient);

        Assert.Equal(tenantId, result.TenantId);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", result.CorrelationId);
        Assert.Collection(
            runner.Commands,
            command => Assert.EndsWith("config --quiet", command, StringComparison.Ordinal),
            command => Assert.EndsWith("up --build --detach", command, StringComparison.Ordinal));
        Assert.Contains("Tenant connectivity self-test passed", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("whatif mode", output.ToString(), StringComparison.Ordinal);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Null(request.Authorization);
                Assert.Equal("{}", request.Body);
            },
            request =>
            {
                Assert.Equal("Bearer test-bearer-token", request.Authorization);
                Assert.Contains("\"method\":\"initialize\"", request.Body, StringComparison.Ordinal);
                Assert.Contains("\"version\":\"0.1.0\"", request.Body, StringComparison.Ordinal);
            },
            request =>
            {
                Assert.Equal("session-1", request.SessionId);
                Assert.Contains("\"method\":\"notifications/initialized\"", request.Body, StringComparison.Ordinal);
            },
            request =>
            {
                Assert.Equal("session-1", request.SessionId);
                Assert.Contains("\"name\":\"get-user-voice-config\"", request.Body, StringComparison.Ordinal);
                Assert.Contains("\"userUpn\":\"demo.user@example.com\"", request.Body, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task Verify_RejectsASetupWithoutTheWhatIfCeilingBeforeStartingCompose()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, ".env"),
            """
            TEAMSPHONE_MCP_BEARER_TOKEN=test-bearer-token
            TEAMSPHONE_MCP_MODE=full
            TEAMSPHONE_MCP_PORT=5199
            TEAMSPHONE_MCP_TENANT_ID=11111111-1111-1111-1111-111111111111
            """);
        var runner = new RecordingProcessRunner();
        using var httpClient = new HttpClient(new QueuedResponseHandler([]));
        var options = new InitVerifyOptions(
            _root,
            ".env",
            "demo.user@example.com",
            StartServer: true,
            TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InitCommand.VerifyAsync(options, TextWriter.Null, runner, httpClient));

        Assert.Contains("TEAMSPHONE_MCP_MODE=whatif", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    private static HttpResponseMessage JsonResponse(object value, string? sessionId = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
        if (sessionId is not null)
        {
            response.Headers.Add("Mcp-Session-Id", sessionId);
        }

        return response;
    }

    private static string ReadEnvironmentValue(string environment, string name)
    {
        var prefix = $"{name}=";
        var line = environment
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return line[prefix.Length..].Trim('"');
    }

    private sealed class RecordingProcessRunner : IInitProcessRunner
    {
        public List<string> Commands { get; } = [];

        public Task<InitProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Commands.Add($"{fileName} {string.Join(' ', arguments)}");
            return Task.FromResult(new InitProcessResult(0));
        }
    }

    private sealed class QueuedResponseHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotEmpty(responses);
            Requests.Add(
                new CapturedRequest(
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds)
                        ? sessionIds.Single()
                        : null,
                    request.Content is null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync(cancellationToken)));
            var response = responses.Dequeue();
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed record CapturedRequest(string? Authorization, string? SessionId, string Body);
}