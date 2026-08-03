using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Audit;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;

namespace TeamsPhoneMcp.Core.Tools;

public sealed class QueryAuditLogMcpServerTool(ToolManifest manifest) : McpServerTool
{
    public const string ToolId = "query-audit-log";

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
        var fromRaw = LocalAuditToolSupport.OptionalString(arguments, "fromUtc");
        var toRaw = LocalAuditToolSupport.OptionalString(arguments, "toUtc");
        var correlationId = Guid.NewGuid().ToString();

        if (!LocalAuditToolSupport.TryParseUtc(fromRaw, out var fromUtc) ||
            !LocalAuditToolSupport.TryParseUtc(toRaw, out var toUtc))
        {
            return ToCallToolResult(QueryAuditLogResult.Failure(
                manifest,
                tenantId,
                correlationId,
                "invalidDateRange",
                "fromUtc and toUtc must be ISO 8601 date-time values."));
        }

        if (fromUtc > toUtc)
        {
            return ToCallToolResult(QueryAuditLogResult.Failure(
                manifest,
                tenantId,
                correlationId,
                "invalidDateRange",
                "fromUtc must not be after toUtc."));
        }

        var filters = JsonSerializer.SerializeToElement(
            new
            {
                fromUtc = fromRaw,
                toUtc = toRaw,
                toolId = LocalAuditToolSupport.OptionalString(arguments, "toolId"),
                status = LocalAuditToolSupport.OptionalString(arguments, "status"),
                clientId = LocalAuditToolSupport.OptionalString(arguments, "clientId"),
            },
            LocalAuditToolSupport.SerializerOptions);
        var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var paginationResolver = services.GetRequiredService<ToolPaginationResolver>();
        var pagination = paginationResolver.Resolve(
            manifest,
            tenantId,
            arguments,
            filters,
            timeProvider.GetUtcNow());
        if (!pagination.IsValid)
        {
            return ToCallToolResult(QueryAuditLogResult.Failure(
                manifest,
                tenantId,
                correlationId,
                pagination.ErrorCode ?? "invalidPagination",
                "The pagination request is invalid or expired."));
        }

        var pageRequest = pagination.Pagination!;
        var page = await services.GetRequiredService<IAuditQueryService>()
            .QueryAsync(
                new AuditQuery(tenantId)
                {
                    FromUtc = fromUtc,
                    ToUtc = toUtc,
                    ToolId = LocalAuditToolSupport.OptionalString(arguments, "toolId"),
                    Status = LocalAuditToolSupport.OptionalString(arguments, "status"),
                    ClientId = LocalAuditToolSupport.OptionalString(arguments, "clientId"),
                    Offset = pageRequest.Offset,
                    Limit = pageRequest.PageSize,
                },
                cancellationToken)
            .ConfigureAwait(false);

        var continuationToken = page.HasMore
            ? paginationResolver.IssueContinuationToken(
                manifest,
                tenantId,
                filters,
                page.Offset + page.Records.Count,
                timeProvider.GetUtcNow())
            : null;
        var records = page.Records.Select(AuditLogEntry.FromRecord).ToArray();
        var result = new QueryAuditLogResult(
            Status: "succeeded",
            ToolId: manifest.Id,
            ToolVersion: manifest.Version,
            TenantId: tenantId,
            CorrelationId: correlationId,
            Summary: $"Returned {records.Length} of {page.TotalCount} matching audit record(s).",
            Records: records,
            TotalCount: page.TotalCount,
            Pagination: new ToolPagination(pageRequest.PageSize, records.Length, page.HasMore, continuationToken),
            ErrorCode: null,
            ErrorMessage: null);
        return ToCallToolResult(result);
    }

    private static CallToolResult ToCallToolResult(QueryAuditLogResult result) =>
        LocalAuditToolSupport.ToCallToolResult(result, result.Summary, result.ErrorCode is not null);
}

public sealed record QueryAuditLogResult(
    string Status,
    string ToolId,
    string ToolVersion,
    string TenantId,
    string CorrelationId,
    string Summary,
    IReadOnlyList<AuditLogEntry> Records,
    int TotalCount,
    ToolPagination? Pagination,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static QueryAuditLogResult Failure(
        ToolManifest manifest,
        string tenantId,
        string correlationId,
        string errorCode,
        string message) =>
        new(
            "failed",
            manifest.Id,
            manifest.Version,
            tenantId,
            correlationId,
            message,
            [],
            0,
            null,
            errorCode,
            message);
}

public sealed record AuditLogEntry(
    DateTimeOffset Timestamp,
    string CorrelationId,
    string? ClientId,
    string ToolId,
    string ToolVersion,
    string Status,
    string? ErrorCode,
    bool DryRun,
    bool Simulated,
    int RiskTier,
    long DurationMs)
{
    public static AuditLogEntry FromRecord(AuditRecord record) =>
        new(
            record.Timestamp,
            record.CorrelationId,
            record.ClientId,
            record.ToolId,
            record.ToolVersion,
            record.Status,
            record.ErrorCode,
            record.DryRun,
            record.Simulated,
            record.RiskTier,
            record.DurationMs);
}