using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Tools;

namespace TeamsPhoneMcp.Core.Manifests;

/// <summary>
/// Enforces manifest/schema parity for hand-written C# tools and produces the
/// same audit record that manifest-driven pipeline tools do, so no tool call can
/// bypass the trail (build spec §9.1).
/// </summary>
public sealed class ManifestValidatingMcpServerTool(McpServerTool innerTool)
    : DelegatingMcpServerTool(innerTool)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var services = request.Services
            ?? throw new InvalidOperationException("The MCP tool request does not provide a service provider.");
        var manifestCatalog = services.GetRequiredService<IToolManifestCatalog>();
        var manifest = manifestCatalog.GetRequired(ProtocolTool.Name);

        var arguments = request.Params?.Arguments;
        var recorder = services.GetService<IToolAuditRecorder>();
        var auditContext = BuildAuditContext(manifest, arguments);

        try
        {
            ToolArgumentValidator.Validate(manifest, arguments);
        }
        catch (Exception ex)
        {
            await RecordAsync(
                recorder,
                auditContext,
                manifest,
                ToolExecutionStatus.Failed,
                "invalidArguments",
                ex.Message,
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        var result = await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);

        var interpreted = InterpretResult(result);
        if (interpreted.CorrelationId is not null)
        {
            auditContext = auditContext with { CorrelationId = interpreted.CorrelationId };
        }

        await RecordAsync(
            recorder,
            auditContext,
            manifest,
            interpreted.Status,
            interpreted.ErrorCode,
            interpreted.Message,
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static ToolAuditContext BuildAuditContext(
        ToolManifest manifest,
        IDictionary<string, JsonElement>? arguments)
    {
        var tenantId = arguments is not null &&
            arguments.TryGetValue("tenantId", out var tenant) &&
            tenant.ValueKind == JsonValueKind.String
                ? tenant.GetString() ?? "unknown"
                : "unknown";

        var business = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (arguments is not null)
        {
            foreach (var pair in arguments)
            {
                if (!ReservedToolArguments.Names.Contains(pair.Key))
                {
                    business[pair.Key] = pair.Value;
                }
            }
        }

        return new ToolAuditContext(
            manifest,
            Guid.NewGuid().ToString(),
            tenantId,
            JsonSerializer.SerializeToElement(business, SerializerOptions),
            Simulated: false);
    }

    /// <summary>
    /// Hand-written tools return their own result shapes, so the status and
    /// correlation id are lifted from the structured content when present — the
    /// audit entry then points at the same identifier the client received.
    /// </summary>
    private static (ToolExecutionStatus Status, string? ErrorCode, string? Message, string? CorrelationId)
        InterpretResult(CallToolResult result)
    {
        var status = result.IsError == true ? ToolExecutionStatus.Failed : ToolExecutionStatus.Succeeded;
        string? errorCode = null;
        string? message = null;
        string? correlationId = null;

        if (TryReadResultObject(result) is { } content)
        {
            if (content.TryGetProperty("correlationId", out var id) && id.ValueKind == JsonValueKind.String)
            {
                correlationId = id.GetString();
            }

            if (content.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String)
            {
                errorCode = code.GetString();
                status = ToolExecutionStatus.Failed;
            }

            if (content.TryGetProperty("errorMessage", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                message = detail.GetString();
            }

            if (content.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String)
            {
                message ??= summary.GetString();
            }
        }

        return (status, errorCode, message, correlationId);
    }

    /// <summary>
    /// Hand-written tools may return their payload as structured content or as a
    /// JSON text block, depending on how the SDK bound the handler; both shapes
    /// are accepted so the audit entry keeps the tool's own correlation id.
    /// </summary>
    private static JsonElement? TryReadResultObject(CallToolResult result)
    {
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } structured)
        {
            return structured;
        }

        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            if (string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }

            try
            {
                var element = JsonDocument.Parse(block.Text).RootElement;
                if (element.ValueKind == JsonValueKind.Object)
                {
                    return element.Clone();
                }
            }
            catch (JsonException)
            {
                // Plain-text results carry no audit metadata; that is fine.
            }
        }

        return null;
    }

    private static async Task RecordAsync(
        IToolAuditRecorder? recorder,
        ToolAuditContext context,
        ToolManifest manifest,
        ToolExecutionStatus status,
        string? errorCode,
        string? message,
        CancellationToken cancellationToken)
    {
        if (recorder is null)
        {
            return;
        }

        var envelope = new ToolResultEnvelope
        {
            Status = status,
            ToolId = manifest.Id,
            ToolVersion = manifest.Version,
            TenantId = Guid.TryParse(context.TenantId, out var tenantGuid) ? tenantGuid : Guid.Empty,
            CorrelationId = context.CorrelationId,
            DryRun = false,
            Summary = message ?? status.ToString(),
            Error = errorCode is null ? null : new ToolError(errorCode, message ?? "The tool call failed."),
        };

        await recorder.RecordAsync(context, envelope, cancellationToken).ConfigureAwait(false);
    }
}