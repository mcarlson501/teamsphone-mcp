using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Audit;
using TeamsPhoneMcp.Core.Manifests;

namespace TeamsPhoneMcp.Core.Tools;

public sealed class GetChangeDetailMcpServerTool(ToolManifest manifest) : McpServerTool
{
    public const string ToolId = "get-change-detail";

    public override Tool ProtocolTool { get; } = ManifestProtocolToolFactory.Build(manifest);

    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var services = request.Services
            ?? throw new InvalidOperationException("The MCP tool request does not provide a service provider.");
        var arguments = request.Params?.Arguments is { } supplied
            ? new Dictionary<string, JsonElement>(supplied, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var tenantId = arguments["tenantId"].GetString()!;
        var requestedCorrelationId = arguments["correlationId"].GetString()!;
        var correlationId = Guid.NewGuid().ToString();
        var detail = await services.GetRequiredService<IAuditQueryService>()
            .GetChangeDetailAsync(tenantId, requestedCorrelationId, cancellationToken)
            .ConfigureAwait(false);

        GetChangeDetailResult result;
        if (detail is null)
        {
            result = new GetChangeDetailResult(
                "failed",
                manifest.Id,
                manifest.Version,
                tenantId,
                correlationId,
                requestedCorrelationId,
                $"No audit record was found for correlation ID '{requestedCorrelationId}'.",
                null,
                "changeNotFound",
                "No matching change record exists in this tenant's audit trail.");
        }
        else
        {
            result = new GetChangeDetailResult(
                "succeeded",
                manifest.Id,
                manifest.Version,
                tenantId,
                correlationId,
                requestedCorrelationId,
                $"Retrieved audit detail for {detail.Record.ToolId}.",
                detail,
                null,
                null);
        }

        return LocalAuditToolSupport.ToCallToolResult(result, result.Summary, result.ErrorCode is not null);
    }
}

public sealed record GetChangeDetailResult(
    string Status,
    string ToolId,
    string ToolVersion,
    string TenantId,
    string CorrelationId,
    string RequestedCorrelationId,
    string Summary,
    AuditChangeDetail? Detail,
    string? ErrorCode,
    string? ErrorMessage);