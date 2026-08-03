using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Audit;
using TeamsPhoneMcp.Core.Manifests;

namespace TeamsPhoneMcp.Core.Tools;

public sealed class ExportAuditReportMcpServerTool(ToolManifest manifest) : McpServerTool
{
    public const string ToolId = "export-audit-report";
    public const string ReportChangeHistoryToolId = "report-change-history";

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
        var fromRaw = arguments["fromUtc"].GetString();
        var toRaw = arguments["toUtc"].GetString();
        var format = arguments["format"].GetString()!;
        var correlationId = Guid.NewGuid().ToString();

        if (!LocalAuditToolSupport.TryParseUtc(fromRaw, out var fromUtc) ||
            !LocalAuditToolSupport.TryParseUtc(toRaw, out var toUtc) ||
            !fromUtc.HasValue ||
            !toUtc.HasValue ||
            fromUtc > toUtc)
        {
            var failure = ExportAuditReportResult.Failure(
                manifest,
                tenantId,
                correlationId,
                "fromUtc and toUtc must define an ordered ISO 8601 date-time range.");
            return LocalAuditToolSupport.ToCallToolResult(failure, failure.Summary, isError: true);
        }

        var page = await services.GetRequiredService<IAuditQueryService>()
            .QueryAsync(
                new AuditQuery(tenantId)
                {
                    FromUtc = fromUtc,
                    ToUtc = toUtc,
                    Limit = int.MaxValue,
                },
                cancellationToken)
            .ConfigureAwait(false);
        var report = format == "csv"
            ? AuditReportRenderer.RenderCsv(page.Records)
            : AuditReportRenderer.RenderMarkdown(tenantId, fromUtc.Value, toUtc.Value, page.Records);
        var result = new ExportAuditReportResult(
            "succeeded",
            manifest.Id,
            manifest.Version,
            tenantId,
            correlationId,
            $"Exported {page.TotalCount} audit record(s) as {format}.",
            format,
            page.TotalCount,
            report,
            null,
            null);
        return LocalAuditToolSupport.ToCallToolResult(result, result.Summary, isError: false);
    }
}

public sealed record ExportAuditReportResult(
    string Status,
    string ToolId,
    string ToolVersion,
    string TenantId,
    string CorrelationId,
    string Summary,
    string? Format,
    int RecordCount,
    string? Report,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ExportAuditReportResult Failure(
        ToolManifest manifest,
        string tenantId,
        string correlationId,
        string message) =>
        new(
            "failed",
            manifest.Id,
            manifest.Version,
            tenantId,
            correlationId,
            message,
            null,
            0,
            null,
            "invalidDateRange",
            message);
}