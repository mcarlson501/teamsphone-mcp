using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;
using TeamsPhoneMcp.Core.Sessions;

namespace TeamsPhoneMcp.Core.Tools;

/// <summary>
/// A manifest-driven MCP tool that routes a tool call through the deterministic
/// write-safety policy and the staged execution pipeline (build spec §6). This is
/// the bridge that lets contributors add tools with only a <c>manifest.yaml</c>
/// and a <c>run.ps1</c> — the host engine is never edited per tool.
/// </summary>
/// <remarks>
/// Tenant context (<c>tenantId</c>, <c>credentialRef</c>) and policy controls
/// (<c>dryRun</c>, <c>whatIf</c>, <c>confirmationToken</c>, <c>blastRadius</c>,
/// <c>allowTier3</c>, <c>maxRiskTier</c>) are supplied per call as reserved
/// arguments. Everything else in the manifest inputs is the tool's business
/// payload, forwarded to <c>run.ps1</c>.
/// </remarks>
public sealed class ManifestPipelineTool : McpServerTool
{
    private static readonly HashSet<string> ReservedArguments =
        new(ReservedToolArguments.Names, StringComparer.Ordinal);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ToolManifest _manifest;

    public ManifestPipelineTool(ToolManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _manifest = manifest;
        ProtocolTool = ManifestProtocolToolFactory.Build(manifest);
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata => Array.Empty<object>();

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var services = request.Services
            ?? throw new InvalidOperationException("The MCP tool request does not provide a service provider.");

        var manifestCatalog = services.GetRequiredService<IToolManifestCatalog>();
        var manifest = manifestCatalog.GetRequired(_manifest.Id);

        var arguments = request.Params?.Arguments?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal) ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        ToolArgumentValidator.Validate(manifest, arguments);

        var reader = new ArgumentReader(arguments, manifest.Id);
        var tenantId = reader.RequireTenantId();
        var credentialRef = reader.RequireCredentialRef();
        var businessParameters = BuildBusinessParameters(arguments);

        var correlationId = Guid.NewGuid().ToString();
        var serverMode = ServerModeCeiling.Resolve(services);

        // The transport's session id reaches tools through the request accessor, not through
        // request.Server, which does not carry it on the Streamable HTTP transport.
        var sessionId = services.GetService<IMcpRequestSessionAccessor>()?.SessionId;
        var clientId = services.GetService<IAuthenticatedClientAccessor>()?.ClientId;
        var sessionWhatIfMode = services
            .GetRequiredService<IMcpSessionPolicyStore>()
            .IsWhatIfMode(sessionId);
        var effectiveWhatIfMode =
            serverMode == ServerModeCeiling.Mode.WhatIf || sessionWhatIfMode;
        var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;

        // Every exit path below writes exactly one audit record (build spec §9.1).
        var auditRecorder = services.GetService<IToolAuditRecorder>();
        var auditContext = new ToolAuditContext(
            manifest,
            correlationId,
            tenantId.ToString(),
            businessParameters,
            Simulated: effectiveWhatIfMode)
        {
            SessionId = sessionId,
            ClientId = clientId,
            ReportedClient = DescribeClient(request),
        };

        var paginationResolver = services.GetRequiredService<ToolPaginationResolver>();
        var pagination = paginationResolver.Resolve(
            manifest,
            tenantId.ToString(),
            arguments,
            businessParameters,
            timeProvider.GetUtcNow());
        if (!pagination.IsValid)
        {
            return await FailureResultAsync(
                manifest,
                tenantId,
                correlationId,
                pagination.ErrorCode ?? "invalidPagination",
                "The pagination request is invalid or expired.",
                auditRecorder,
                auditContext,
                cancellationToken).ConfigureAwait(false);
        }

        if (serverMode == ServerModeCeiling.Mode.ReadOnly && manifest.RiskTier > 0)
        {
            return await FailureResultAsync(
                manifest,
                tenantId,
                correlationId,
                "readOnlyMode",
                "The server is running in read-only mode; this tool is not available.",
                auditRecorder,
                auditContext,
                cancellationToken).ConfigureAwait(false);
        }

        var policyEngine = services.GetRequiredService<WritePolicyEngine>();

        var decision = policyEngine.Evaluate(
            manifest,
            new WritePolicyRequest(
                tenantId.ToString(),
                businessParameters,
                reader.OptionalBool("dryRun"),
                reader.OptionalBool("whatIf"),
                reader.OptionalString("confirmationToken"),
                reader.OptionalInt("blastRadius", defaultValue: 1),
                reader.OptionalBool("allowTier3") ?? false,
                reader.OptionalInt("maxRiskTier", defaultValue: 3),
                SessionWhatIfMode: effectiveWhatIfMode)
            {
                Binding = new ConfirmationTokenBinding(sessionId, clientId)
            },
            timeProvider.GetUtcNow());

        var runner = services.GetRequiredService<IToolPipelineRunner>();
        var pipelineRequest = new ToolPipelineRequest(
            manifest,
            businessParameters.GetRawText(),
            new TenantSessionContext(tenantId, credentialRef),
            decision,
            correlationId)
        {
            Pagination = pagination.Pagination
        };

        var envelope = await runner.ExecuteAsync(pipelineRequest, cancellationToken).ConfigureAwait(false);
        if (envelope.Status == ToolExecutionStatus.Succeeded &&
            envelope.Pagination is { HasMore: true } page &&
            envelope.NextOffset.HasValue)
        {
            var continuationToken = paginationResolver.IssueContinuationToken(
                manifest,
                tenantId.ToString(),
                businessParameters,
                envelope.NextOffset.Value,
                timeProvider.GetUtcNow());
            envelope = envelope with
            {
                Pagination = page with { ContinuationToken = continuationToken }
            };
        }

        if (auditRecorder is not null)
        {
            await auditRecorder.RecordAsync(auditContext, envelope, cancellationToken).ConfigureAwait(false);
        }

        return ToCallToolResult(envelope);
    }

    /// <summary>
    /// The client's self-reported name/version. Recorded alongside, never in place of, the
    /// server-derived client id, because anything the client asserts is unverified.
    /// </summary>
    private static string? DescribeClient(RequestContext<CallToolRequestParams> request)
    {
        var clientInfo = request.Server?.ClientInfo;
        if (clientInfo is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(clientInfo.Version)
            ? clientInfo.Name
            : $"{clientInfo.Name}/{clientInfo.Version}";
    }

    /// <summary>
    /// Builds the canonical business payload with reserved arguments removed and
    /// keys ordered, so the confirmation-token hash is stable across the dry-run
    /// and execute calls.
    /// </summary>
    private static JsonElement BuildBusinessParameters(IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        var ordered = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments)
            {
                if (!ReservedArguments.Contains(key))
                {
                    ordered[key] = value;
                }
            }
        }

        var node = new JsonObject();
        foreach (var (key, value) in ordered)
        {
            node[key] = JsonNode.Parse(value.GetRawText());
        }

        return JsonSerializer.SerializeToElement(node, SerializerOptions);
    }

    private static async ValueTask<CallToolResult> FailureResultAsync(
        ToolManifest manifest,
        Guid tenantId,
        string correlationId,
        string errorCode,
        string message,
        IToolAuditRecorder? auditRecorder,
        ToolAuditContext auditContext,
        CancellationToken cancellationToken)
    {
        var envelope = new ToolResultEnvelope
        {
            Status = ToolExecutionStatus.Failed,
            ToolId = manifest.Id,
            ToolVersion = manifest.Version,
            TenantId = tenantId,
            CorrelationId = correlationId,
            DryRun = false,
            Summary = message,
            Error = new ToolError(errorCode, message),
        };

        if (auditRecorder is not null)
        {
            await auditRecorder.RecordAsync(auditContext, envelope, cancellationToken).ConfigureAwait(false);
        }

        return ToCallToolResult(envelope);
    }

    private static CallToolResult ToCallToolResult(ToolResultEnvelope envelope)
    {
        var structured = JsonSerializer.SerializeToElement(envelope, SerializerOptions);
        return new CallToolResult
        {
            StructuredContent = structured,
            IsError = envelope.Status == ToolExecutionStatus.Failed,
            Content = [new TextContentBlock { Text = envelope.Summary }],
        };
    }

    private sealed class ArgumentReader(IEnumerable<KeyValuePair<string, JsonElement>>? arguments, string toolId)
    {
        private readonly IReadOnlyDictionary<string, JsonElement> _arguments =
            arguments?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        public Guid RequireTenantId()
        {
            var value = OptionalString("tenantId");
            if (!Guid.TryParse(value, out var tenantId) || tenantId == Guid.Empty)
            {
                throw new ModelContextProtocol.McpException(
                    $"Tool '{toolId}' requires 'tenantId' to be a valid tenant GUID.");
            }

            return tenantId;
        }

        public string RequireCredentialRef()
        {
            var value = OptionalString("credentialRef");
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ModelContextProtocol.McpException(
                    $"Tool '{toolId}' requires a non-empty 'credentialRef'.");
            }

            return value;
        }

        public string? OptionalString(string name) =>
            _arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        public bool? OptionalBool(string name) =>
            _arguments.TryGetValue(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

        public int OptionalInt(string name, int defaultValue) =>
            _arguments.TryGetValue(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var parsed)
                ? parsed
                : defaultValue;
    }
}
