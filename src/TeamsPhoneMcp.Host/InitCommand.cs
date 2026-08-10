using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamsPhoneMcp.Host;

internal static partial class InitCommand
{
    private const string DefaultCredentialRef = "default";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0 || IsHelp(args[0]))
        {
            await WriteUsageAsync(args.Length == 0 ? error : output);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            if (string.Equals(args[0], "prepare", StringComparison.OrdinalIgnoreCase))
            {
                var options = ParsePrepareOptions(args[1..]);
                var prepared = await PrepareAsync(options, output, cancellationToken);
                await WriteNextStepsAsync(prepared, output);
                return 0;
            }

            if (string.Equals(args[0], "verify", StringComparison.OrdinalIgnoreCase))
            {
                var options = ParseVerifyOptions(args[1..]);
                using var httpClient = new HttpClient();
                await VerifyAsync(
                    options,
                    output,
                    new SystemInitProcessRunner(),
                    httpClient,
                    cancellationToken);
                return 0;
            }

            await error.WriteLineAsync($"error: unknown init action '{args[0]}'.");
            await WriteUsageAsync(error);
            return 2;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or
            CryptographicException or
            HttpRequestException or
            JsonException or
            TimeoutException or
            InvalidOperationException)
        {
            await error.WriteLineAsync($"error: {exception.Message}");
            return 1;
        }
    }

    internal static async Task<InitPreparation> PrepareAsync(
        InitPrepareOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        ValidateOptions(options);

        var projectDirectory = Path.GetFullPath(options.ProjectDirectory);
        var environmentFilePath = Path.GetFullPath(options.EnvironmentFilePath, projectDirectory);
        var certificateDirectory = Path.GetFullPath(options.CertificateDirectory);
        var certificateBaseName = $"teamsphone-mcp-{options.CredentialRef}";
        var publicCertificatePath = Path.Combine(certificateDirectory, $"{certificateBaseName}.cer");
        var privateCertificatePath = Path.Combine(certificateDirectory, $"{certificateBaseName}.pfx");

        EnsureWritable(environmentFilePath, options.Force);
        EnsureWritable(publicCertificatePath, options.Force);
        EnsureWritable(privateCertificatePath, options.Force);

        Directory.CreateDirectory(certificateDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(environmentFilePath)!);
        SetDirectoryPermissions(certificateDirectory);

        var subject = $"CN={certificateBaseName}";
        var certificatePassword = CreateSecret();
        using var certificate = CreateCertificate(subject);

        await File.WriteAllBytesAsync(
            publicCertificatePath,
            certificate.Export(X509ContentType.Cert),
            cancellationToken);
        await WriteSecretFileAsync(
            privateCertificatePath,
            certificate.Export(X509ContentType.Pkcs12, certificatePassword),
            cancellationToken);

        var bearerToken = CreateSecret();
        var signingKey = CreateSecret();
        var environment = BuildEnvironmentFile(
            options,
            privateCertificatePath,
            certificatePassword,
            bearerToken,
            signingKey);
        await WriteSecretFileAsync(
            environmentFilePath,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(environment),
            cancellationToken);

        var preparation = new InitPreparation(
            options.TenantId,
            options.ClientId,
            options.CredentialRef,
            subject,
            certificate.Thumbprint,
            certificate.NotAfter.ToUniversalTime(),
            publicCertificatePath,
            privateCertificatePath,
            environmentFilePath);

        await output.WriteLineAsync("Prepared TeamsPhone MCP local configuration.");
        await output.WriteLineAsync($"Public certificate: {publicCertificatePath}");
        await output.WriteLineAsync($"Private certificate: {privateCertificatePath}");
        await output.WriteLineAsync($"Compose environment: {environmentFilePath}");
        await output.WriteLineAsync($"Certificate subject: {subject}");
        await output.WriteLineAsync($"Certificate thumbprint: {certificate.Thumbprint}");
        await output.WriteLineAsync($"Certificate expires UTC: {certificate.NotAfter.ToUniversalTime():O}");
        await output.WriteLineAsync("Server execution ceiling: whatif");

        return preparation;
    }

    internal static async Task<InitVerification> VerifyAsync(
        InitVerifyOptions options,
        TextWriter output,
        IInitProcessRunner processRunner,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(httpClient);

        var projectDirectory = Path.GetFullPath(options.ProjectDirectory);
        var environmentFilePath = Path.GetFullPath(options.EnvironmentFilePath, projectDirectory);
        if (!File.Exists(Path.Combine(projectDirectory, "docker-compose.yml")))
        {
            throw new ArgumentException("ProjectDirectory must contain docker-compose.yml.");
        }

        var environment = await ReadEnvironmentFileAsync(environmentFilePath, cancellationToken);
        var tenantId = ReadRequiredGuid(environment, "TEAMSPHONE_MCP_TENANT_ID");
        var bearerToken = ReadRequiredValue(environment, "TEAMSPHONE_MCP_BEARER_TOKEN");
        var serverMode = ReadRequiredValue(environment, "TEAMSPHONE_MCP_MODE");
        if (!string.Equals(serverMode, "whatif", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Guided verification requires TEAMSPHONE_MCP_MODE=whatif. Restore the operator ceiling before testing tenant connectivity.");
        }
        var portText = ReadRequiredValue(environment, "TEAMSPHONE_MCP_PORT");
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("TEAMSPHONE_MCP_PORT must be between 1 and 65535.");
        }

        if (options.StartServer)
        {
            await RunComposeAsync(
                processRunner,
                projectDirectory,
                environmentFilePath,
                ["config", "--quiet"],
                cancellationToken);
            await RunComposeAsync(
                processRunner,
                projectDirectory,
                environmentFilePath,
                ["up", "--build", "--detach"],
                cancellationToken);
            await output.WriteLineAsync("Compose configuration validated and TeamsPhone MCP started.");
        }

        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        await WaitForServerAsync(httpClient, endpoint, options.StartupTimeout, cancellationToken);
        await output.WriteLineAsync($"HTTP authentication gate is responding at {endpoint}.");

        var sessionId = await InitializeAsync(httpClient, endpoint, bearerToken, cancellationToken);
        await SendInitializedAsync(httpClient, endpoint, bearerToken, sessionId, cancellationToken);
        var toolResult = await CallUserVoiceConfigAsync(
            httpClient,
            endpoint,
            bearerToken,
            sessionId,
            tenantId,
            options.UserUpn,
            cancellationToken);

        var structuredContent = toolResult
            .GetProperty("result")
            .GetProperty("structuredContent");
        var status = structuredContent.GetProperty("status").GetString();
        if (!string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
        {
            var errorCode = structuredContent.TryGetProperty("error", out var errorObject) &&
                            errorObject.ValueKind == JsonValueKind.Object &&
                            errorObject.TryGetProperty("code", out var nestedCode)
                ? nestedCode.GetString()
                : null;
            throw new InvalidOperationException(BuildVerificationFailure(errorCode));
        }

        var correlationId = structuredContent.GetProperty("correlationId").GetString()
            ?? throw new InvalidOperationException("The connectivity probe returned no correlationId.");
        await output.WriteLineAsync("Tenant connectivity self-test passed.");
        await output.WriteLineAsync($"Tool: get-user-voice-config");
        await output.WriteLineAsync($"Correlation ID: {correlationId}");
        await output.WriteLineAsync("The server remains in whatif mode; write execution is disabled by the operator ceiling.");

        return new InitVerification(endpoint, tenantId, options.UserUpn, correlationId);
    }

    private static InitPrepareOptions ParsePrepareOptions(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var force = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--force", StringComparison.OrdinalIgnoreCase))
            {
                force = true;
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Option '{argument}' requires a value.");
            }

            values[argument] = args[++index];
        }

        var tenantId = ParseRequiredGuid(values, "--tenant-id");
        var clientId = ParseRequiredGuid(values, "--client-id");
        var projectDirectory = GetValue(values, "--project-directory") ?? Environment.CurrentDirectory;
        var environmentFilePath = GetValue(values, "--env-file") ?? ".env";
        var certificateDirectory = GetValue(values, "--certificate-directory") ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config",
                "teamsphone-mcp");
        var credentialRef = DefaultCredentialRef;

        var knownOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--tenant-id",
            "--client-id",
            "--project-directory",
            "--env-file",
            "--certificate-directory",
        };
        var unknownOption = values.Keys.FirstOrDefault(key => !knownOptions.Contains(key));
        if (unknownOption is not null)
        {
            throw new ArgumentException($"Unknown option '{unknownOption}'.");
        }

        return new InitPrepareOptions(
            tenantId,
            clientId,
            credentialRef,
            projectDirectory,
            environmentFilePath,
            certificateDirectory,
            force);
    }

    private static InitVerifyOptions ParseVerifyOptions(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var startServer = true;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--no-start", StringComparison.OrdinalIgnoreCase))
            {
                startServer = false;
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Option '{argument}' requires a value.");
            }

            values[argument] = args[++index];
        }

        var knownOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--user-upn",
            "--project-directory",
            "--env-file",
        };
        var unknownOption = values.Keys.FirstOrDefault(key => !knownOptions.Contains(key));
        if (unknownOption is not null)
        {
            throw new ArgumentException($"Unknown option '{unknownOption}'.");
        }

        var userUpn = GetValue(values, "--user-upn");
        if (string.IsNullOrWhiteSpace(userUpn) ||
            userUpn.ContainsAny('\r', '\n') ||
            !userUpn.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException("--user-upn must be a valid demo-tenant UPN.");
        }

        return new InitVerifyOptions(
            GetValue(values, "--project-directory") ?? Environment.CurrentDirectory,
            GetValue(values, "--env-file") ?? ".env",
            userUpn,
            startServer,
            TimeSpan.FromMinutes(3));
    }

    private static Guid ParseRequiredGuid(IReadOnlyDictionary<string, string> values, string option)
    {
        var value = GetValue(values, option);
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException($"{option} must be a non-empty GUID.");
        }

        return parsed;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string option) =>
        values.TryGetValue(option, out var value) ? value : null;

    private static void ValidateOptions(InitPrepareOptions options)
    {
        if (options.TenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId must not be empty.");
        }

        if (options.ClientId == Guid.Empty)
        {
            throw new ArgumentException("ClientId must not be empty.");
        }

        if (!CredentialRefPattern().IsMatch(options.CredentialRef))
        {
            throw new ArgumentException(
                "CredentialRef must start with an alphanumeric character and contain only letters, numbers, '.', '_' or '-'.");
        }

        if (!string.Equals(options.CredentialRef, DefaultCredentialRef, StringComparison.Ordinal))
        {
            throw new ArgumentException("The Compose bootstrap currently supports credentialRef 'default' only.");
        }

        if (!File.Exists(Path.Combine(Path.GetFullPath(options.ProjectDirectory), "docker-compose.yml")))
        {
            throw new ArgumentException("ProjectDirectory must contain docker-compose.yml.");
        }
    }

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            subject,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
        var enhancedKeyUsages = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.2", "Client Authentication"),
        };
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(enhancedKeyUsages, critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    private static string BuildEnvironmentFile(
        InitPrepareOptions options,
        string privateCertificatePath,
        string certificatePassword,
        string bearerToken,
        string signingKey) =>
        string.Join(
            '\n',
            "# Generated by teamsphone-mcp init prepare. Contains secrets; do not commit.",
            $"TEAMSPHONE_MCP_BEARER_TOKEN={QuoteEnvironmentValue(bearerToken)}",
            $"TEAMSPHONE_MCP_CONFIRMATION_TOKEN_KEY={QuoteEnvironmentValue(signingKey)}",
            "TEAMSPHONE_MCP_MODE=whatif",
            "TEAMSPHONE_MCP_PORT=5199",
            $"TEAMSPHONE_MCP_TENANT_ID={options.TenantId:D}",
            $"TEAMSPHONE_MCP_CLIENT_ID={options.ClientId:D}",
            $"TEAMSPHONE_MCP_CERTIFICATE_PATH={QuoteEnvironmentValue(privateCertificatePath)}",
            $"TEAMSPHONE_MCP_CERTIFICATE_PASSWORD={QuoteEnvironmentValue(certificatePassword)}",
            string.Empty);

    private static string QuoteEnvironmentValue(string value)
    {
        if (value.ContainsAny('\r', '\n'))
        {
            throw new ArgumentException("Environment values must not contain newlines.");
        }

        return SafeEnvironmentValuePattern().IsMatch(value)
            ? value
            : $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string CreateSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static async Task<IReadOnlyDictionary<string, string>> ReadEnvironmentFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Compose environment file '{path}' was not found.", path);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidOperationException($"Invalid entry in '{path}'. Expected NAME=value.");
            }

            var name = trimmed[..separator];
            var value = trimmed[(separator + 1)..];
            values[name] = UnquoteEnvironmentValue(value);
        }

        return values;
    }

    private static string UnquoteEnvironmentValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return value;
    }

    private static string ReadRequiredValue(IReadOnlyDictionary<string, string> environment, string name)
    {
        if (!environment.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is missing from the Compose environment file.");
        }

        return value;
    }

    private static Guid ReadRequiredGuid(IReadOnlyDictionary<string, string> environment, string name)
    {
        var value = ReadRequiredValue(environment, name);
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new InvalidOperationException($"{name} must be a non-empty GUID.");
        }

        return parsed;
    }

    private static async Task RunComposeAsync(
        IInitProcessRunner processRunner,
        string projectDirectory,
        string environmentFilePath,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "compose",
            "--project-directory",
            projectDirectory,
            "--env-file",
            environmentFilePath,
        };
        arguments.AddRange(command);

        var result = await processRunner.RunAsync("docker", arguments, projectDirectory, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker compose {string.Join(' ', command)} failed with exit code {result.ExitCode}. " +
                "Run the command directly for details; generated secrets were not printed.");
        }
    }

    private static async Task WaitForServerAsync(
        HttpClient client,
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var request = CreateMcpRequest(endpoint, bearerToken: null, new { });
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException(
            $"TeamsPhone MCP did not expose its authenticated HTTP endpoint at {endpoint} within {timeout.TotalSeconds:0} seconds.");
    }

    private static async Task<string> InitializeAsync(
        HttpClient client,
        Uri endpoint,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateMcpRequest(
            endpoint,
            bearerToken,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new { name = "teamsphone-mcp-init", version = GetProductVersion() },
                },
            });
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await ReadJsonRpcPayloadAsync(response, cancellationToken);
        ThrowIfJsonRpcError(payload, "initialize");

        return response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.Single()
            : throw new InvalidOperationException("The server returned no Mcp-Session-Id during initialize.");
    }

    private static async Task SendInitializedAsync(
        HttpClient client,
        Uri endpoint,
        string bearerToken,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var request = CreateMcpRequest(
            endpoint,
            bearerToken,
            new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized",
                @params = new { },
            },
            sessionId);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> CallUserVoiceConfigAsync(
        HttpClient client,
        Uri endpoint,
        string bearerToken,
        string sessionId,
        Guid tenantId,
        string userUpn,
        CancellationToken cancellationToken)
    {
        using var request = CreateMcpRequest(
            endpoint,
            bearerToken,
            new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = "get-user-voice-config",
                    arguments = new
                    {
                        tenantId = tenantId.ToString("D"),
                        credentialRef = DefaultCredentialRef,
                        userUpn,
                    },
                },
            },
            sessionId);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await ReadJsonRpcPayloadAsync(response, cancellationToken);
        ThrowIfJsonRpcError(payload, "get-user-voice-config");
        return payload;
    }

    private static HttpRequestMessage CreateMcpRequest(
        Uri endpoint,
        string? bearerToken,
        object payload,
        string? sessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        if (sessionId is not null)
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
            request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        }

        return request;
    }

    private static async Task<JsonElement> ReadJsonRpcPayloadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = body.TrimStart().StartsWith('{')
            ? body
            : body
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .First(line => line.StartsWith("data:", StringComparison.Ordinal))["data:".Length..]
                .Trim();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void ThrowIfJsonRpcError(JsonElement payload, string operation)
    {
        if (payload.TryGetProperty("error", out _))
        {
            throw new InvalidOperationException($"The MCP {operation} request was rejected. Check server logs for the sanitized error.");
        }
    }

    private static string BuildVerificationFailure(string? errorCode) => errorCode switch
    {
        "authenticationFailed" =>
            "Tenant authentication failed. Confirm the public certificate is uploaded, the PFX password in .env is current, admin consent is granted, and the service principal has the Teams Communications Administrator role.",
        "toolExecutionFailed" =>
            "The tenant connection succeeded but the probe tool failed. Confirm Graph permissions and Teams role propagation, then inspect the container logs by correlation ID.",
        _ =>
            $"The tenant connectivity probe returned status other than Succeeded{(errorCode is null ? "." : $" ({errorCode}).")}",
    };

    private static void EnsureWritable(string path, bool force)
    {
        if (File.Exists(path) && !force)
        {
            throw new IOException($"'{path}' already exists. Re-run with --force to replace generated setup files.");
        }
    }

    private static void SetDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var requiredMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if (File.GetUnixFileMode(path) != requiredMode)
            {
                File.SetUnixFileMode(path, requiredMode);
            }
        }
    }

    private static async Task WriteSecretFileAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllBytesAsync(path, content.ToArray(), cancellationToken);
            return;
        }

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (File.Exists(path))
        {
            File.SetUnixFileMode(path, mode);
        }

        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = mode,
            });
        await stream.WriteAsync(content, cancellationToken);
    }

    private static async Task WriteNextStepsAsync(InitPreparation preparation, TextWriter output)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync("Complete these Entra steps:");
        await output.WriteLineAsync("1. Open the app registration matching this client ID:");
        await output.WriteLineAsync($"   {preparation.ClientId:D}");
        await output.WriteLineAsync($"2. Upload the public certificate: {preparation.PublicCertificatePath}");
        await output.WriteLineAsync($"   Confirm thumbprint: {preparation.Thumbprint}");
        await output.WriteLineAsync("3. Grant Microsoft Graph application permissions User.Read.All and Organization.Read.All.");
        await output.WriteLineAsync("4. Add CallRecords.Read.All when testing PSTN usage or call quality, then grant admin consent.");
        await output.WriteLineAsync("5. Assign the service principal the Teams Communications Administrator role.");
        await output.WriteLineAsync("6. Allow propagation time, then run init verify with a demo-tenant user UPN.");
        await output.WriteLineAsync();
        await output.WriteLineAsync("No private key or generated secret was printed. Keep the PFX and .env files private.");
    }

    private static Task WriteUsageAsync(TextWriter writer) => writer.WriteLineAsync(
        "Usage:\n" +
        "  teamsphone-mcp init prepare --tenant-id <guid> --client-id <guid> [options]\n" +
        "\n" +
        "Prepare options:\n" +
        "  --project-directory <path>      Source directory containing docker-compose.yml\n" +
        "  --env-file <path>               Compose environment file (default: .env)\n" +
        "  --certificate-directory <path>  Private certificate directory (default: ~/.config/teamsphone-mcp)\n" +
        "  --force                         Replace files generated by a previous prepare\n" +
        "\n" +
        "  teamsphone-mcp init verify --user-upn <demo-user-upn> [options]\n" +
        "\n" +
        "Verify options:\n" +
        "  --project-directory <path>      Source directory containing docker-compose.yml\n" +
        "  --env-file <path>               Generated Compose environment file (default: .env)\n" +
        "  --no-start                      Verify an already-running server without starting Compose");

    private static string GetProductVersion()
    {
        var informationalVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return informationalVersion?.Split('+', 2)[0]
            ?? throw new InvalidOperationException("The product version is missing from the host assembly.");
    }

    private static bool IsHelp(string value) =>
        string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialRefPattern();

    [GeneratedRegex("^[A-Za-z0-9_./:+@=-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeEnvironmentValuePattern();
}

internal sealed record InitPrepareOptions(
    Guid TenantId,
    Guid ClientId,
    string CredentialRef,
    string ProjectDirectory,
    string EnvironmentFilePath,
    string CertificateDirectory,
    bool Force);

internal sealed record InitPreparation(
    Guid TenantId,
    Guid ClientId,
    string CredentialRef,
    string Subject,
    string Thumbprint,
    DateTime CertificateExpiresUtc,
    string PublicCertificatePath,
    string PrivateCertificatePath,
    string EnvironmentFilePath);

internal sealed record InitVerifyOptions(
    string ProjectDirectory,
    string EnvironmentFilePath,
    string UserUpn,
    bool StartServer,
    TimeSpan StartupTimeout);

internal sealed record InitVerification(
    Uri Endpoint,
    Guid TenantId,
    string UserUpn,
    string CorrelationId);

internal interface IInitProcessRunner
{
    Task<InitProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed record InitProcessResult(int ExitCode);

internal sealed class SystemInitProcessRunner : IInitProcessRunner
{
    public async Task<InitProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        await process.WaitForExitAsync(cancellationToken);
        return new InitProcessResult(process.ExitCode);
    }
}

internal static class StringExtensions
{
    public static bool ContainsAny(this string value, params char[] characters) =>
        value.IndexOfAny(characters) >= 0;
}