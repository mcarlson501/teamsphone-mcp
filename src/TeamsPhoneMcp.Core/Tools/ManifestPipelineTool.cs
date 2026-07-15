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
    private static readonly HashSet<string> ReservedArguments = new(StringComparer.Ordinal)
    {
        "tenantId",
        "credentialRef",
        "dryRun",
        "whatIf",
        "confirmationToken",
        "blastRadius",
        "allowTier3",
        "maxRiskTier",
    };

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ToolManifest _manifest;

    public ManifestPipelineTool(ToolManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _manifest = manifest;
        ProtocolTool = BuildProtocolTool(manifest);
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

        var arguments = request.Params?.Arguments;
        ToolArgumentValidator.Validate(manifest, arguments);

        var reader = new ArgumentReader(arguments, manifest.Id);
        var tenantId = reader.RequireTenantId();
        var credentialRef = reader.RequireCredentialRef();
        var businessParameters = BuildBusinessParameters(arguments);

        var correlationId = Guid.NewGuid().ToString();
        var serverMode = ServerModeCeiling.Resolve(services);

        if (serverMode == ServerModeCeiling.Mode.ReadOnly && manifest.RiskTier > 0)
        {
            return FailureResult(
                manifest,
                tenantId,
                correlationId,
                "readOnlyMode",
                "The server is running in read-only mode; this tool is not available.");
        }

        var policyEngine = services.GetRequiredService<WritePolicyEngine>();
        var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;

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
                SessionWhatIfMode: serverMode == ServerModeCeiling.Mode.WhatIf),
            timeProvider.GetUtcNow());

        var runner = services.GetRequiredService<IToolPipelineRunner>();
        var pipelineRequest = new ToolPipelineRequest(
            manifest,
            businessParameters.GetRawText(),
            new TenantSessionContext(tenantId, credentialRef),
            decision,
            correlationId);

        var envelope = await runner.ExecuteAsync(pipelineRequest, cancellationToken).ConfigureAwait(false);
        return ToCallToolResult(envelope);
    }

    private static Tool BuildProtocolTool(ToolManifest manifest)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var (name, input) in manifest.Inputs)
        {
            var schema = new JsonObject { ["type"] = MapSchemaType(input.Type) };
            if (!string.IsNullOrWhiteSpace(input.Format))
            {
                schema["format"] = input.Format;
            }

            properties[name] = schema;
            if (input.Required)
            {
                required.Add(name);
            }
        }

        var schemaNode = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
        {
            schemaNode["required"] = required;
        }

        return new Tool
        {
            Name = manifest.Id,
            Description = manifest.Summary,
            InputSchema = JsonSerializer.SerializeToElement(schemaNode, SerializerOptions),
            Annotations = new ToolAnnotations
            {
                Title = manifest.Id,
                ReadOnlyHint = manifest.Annotations.ReadOnlyHint,
                DestructiveHint = manifest.Annotations.DestructiveHint,
                IdempotentHint = manifest.Annotations.IdempotentHint,
            },
        };
    }

    private static string MapSchemaType(string manifestType) => manifestType switch
    {
        "string" => "string",
        "integer" => "integer",
        "number" => "number",
        "boolean" => "boolean",
        _ => "string",
    };

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

    private CallToolResult FailureResult(
        ToolManifest manifest,
        Guid tenantId,
        string correlationId,
        string errorCode,
        string message)
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
