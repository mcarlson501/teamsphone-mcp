using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core.Manifests;

namespace TeamsPhoneMcp.Core.Tools;

public sealed class GetPstnUsageMcpServerTool(ToolManifest manifest) : McpServerTool
{
    public const string ToolId = "get-pstn-usage";

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
        var correlationId = Guid.NewGuid().ToString();

        if (!GraphDiagnosticToolSupport.TryTenantContext(arguments, out var context))
        {
            return ToCallToolResult(PstnUsageResult.Failure(
                manifest,
                tenantId,
                correlationId,
                "invalidTenant",
                "tenantId must be a non-empty GUID."));
        }

        if (!GraphDiagnosticToolSupport.TryUtcRange(
                arguments,
                TimeSpan.FromDays(90),
                out var fromUtc,
                out var toUtc,
                out var rangeError))
        {
            return ToCallToolResult(PstnUsageResult.Failure(
                manifest,
                tenantId,
                correlationId,
                "invalidDateRange",
                rangeError));
        }

        var phoneNumber = GraphDiagnosticToolSupport.OptionalString(arguments, "phoneNumber");
        if (!string.IsNullOrWhiteSpace(phoneNumber) && !GraphDiagnosticToolSupport.IsE164(phoneNumber))
        {
            return ToCallToolResult(PstnUsageResult.Failure(
                manifest,
                tenantId,
                correlationId,
                "invalidPhoneNumber",
                "phoneNumber must be in E.164 format, for example +15551234567."));
        }

        try
        {
            var data = await services.GetRequiredService<IGraphCallRecordsClient>()
                .GetPstnCallsAsync(context!, fromUtc, toUtc, cancellationToken)
                .ConfigureAwait(false);
            var userUpn = GraphDiagnosticToolSupport.OptionalString(arguments, "userUpn");
            var calls = data.PstnCalls
                .Select(call => PstnUsageCall.FromGraph(call, "pstn"))
                .Concat(data.DirectRoutingCalls.Select(call => PstnUsageCall.FromGraph(call, "directRouting")))
                .Where(call => call.StartDateTime is { } start && start >= fromUtc && start < toUtc)
                .Where(call => Matches(call, userUpn, phoneNumber))
                .OrderByDescending(call => call.StartDateTime)
                .ToArray();
            var failedCalls = calls.Count(call => call.Successful == false);
            var findings = new List<GraphDiagnosticFinding>();
            if (failedCalls > 0)
            {
                findings.Add(new GraphDiagnosticFinding(
                    "warning",
                    "directRoutingFailures",
                    $"{failedCalls} Direct Routing call attempt(s) failed in the selected window.",
                    "Repeated SIP failures can indicate trunk, route, number translation, or carrier problems.",
                    "Review finalSipCode and finalSipCodePhrase, then verify the affected PSTN route and SBC health."));
            }

            if (data.Truncated)
            {
                findings.Add(new GraphDiagnosticFinding(
                    "warning",
                    "resultsTruncated",
                    "The safety limit was reached before all matching PSTN rows were processed.",
                    "Totals and findings may not represent the entire requested window.",
                    "Retry with a narrower UTC date range; Graph paging links are intentionally not exposed."));
            }

            var result = new PstnUsageResult(
                "succeeded",
                manifest.Id,
                manifest.Version,
                tenantId,
                correlationId,
                $"Returned {calls.Length} matching PSTN call(s) from Calling Plans, Operator Connect, and Direct Routing{(data.Truncated ? "; results are truncated" : string.Empty)}.",
                fromUtc,
                toUtc,
                calls,
                new PstnUsageTotals(
                    calls.Length,
                    calls.Count(call => call.Source == "pstn"),
                    calls.Count(call => call.Source == "directRouting"),
                    failedCalls,
                    calls.Sum(call => (long)call.DurationSeconds),
                    calls.Where(call => call.Charge is not null)
                        .GroupBy(call => call.Currency ?? "unspecified", StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group => group.Key,
                            group => Math.Round(group.Sum(call => call.Charge!.Value), 4),
                            StringComparer.OrdinalIgnoreCase)),
                findings,
                data.Truncated,
                null,
                null);
            return ToCallToolResult(result);
        }
        catch (GraphCallRecordsException ex)
        {
            return ToCallToolResult(PstnUsageResult.Failure(
                manifest,
                tenantId,
                correlationId,
                ex.ErrorCode,
                ex.Message));
        }
    }

    private static bool Matches(PstnUsageCall call, string? userUpn, string? phoneNumber) =>
        (string.IsNullOrWhiteSpace(userUpn) ||
            string.Equals(call.UserPrincipalName, userUpn, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(phoneNumber) ||
            string.Equals(call.CallerNumber, phoneNumber, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(call.CalleeNumber, phoneNumber, StringComparison.OrdinalIgnoreCase));

    private static CallToolResult ToCallToolResult(PstnUsageResult result) =>
        LocalAuditToolSupport.ToCallToolResult(result, result.Summary, result.ErrorCode is not null);
}

public sealed record PstnUsageResult(
    string Status,
    string ToolId,
    string ToolVersion,
    string TenantId,
    string CorrelationId,
    string Summary,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    IReadOnlyList<PstnUsageCall> Calls,
    PstnUsageTotals? Totals,
    IReadOnlyList<GraphDiagnosticFinding> Findings,
    bool Truncated,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static PstnUsageResult Failure(
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
            null,
            null,
            [],
            null,
            [],
            false,
            errorCode,
            message);
}

public sealed record PstnUsageTotals(
    int TotalCalls,
    int PstnCalls,
    int DirectRoutingCalls,
    int FailedCalls,
    long DurationSeconds,
    IReadOnlyDictionary<string, double> ChargesByCurrency);

public sealed record PstnUsageCall(
    string Source,
    string? Id,
    string? UserPrincipalName,
    string? UserDisplayName,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime,
    int DurationSeconds,
    string? CallType,
    string? CallerNumber,
    string? CalleeNumber,
    bool? Successful,
    string? Operator,
    double? Charge,
    string? Currency,
    string? DestinationName,
    int? FinalSipCode,
    string? FinalSipCodePhrase)
{
    public static PstnUsageCall FromGraph(JsonElement row, string source) =>
        new(
            source,
            GraphDiagnosticToolSupport.StringProperty(row, "id"),
            GraphDiagnosticToolSupport.StringProperty(row, "userPrincipalName"),
            GraphDiagnosticToolSupport.StringProperty(row, "userDisplayName"),
            GraphDiagnosticToolSupport.DateProperty(row, "startDateTime"),
            GraphDiagnosticToolSupport.DateProperty(row, "endDateTime"),
            GraphDiagnosticToolSupport.IntProperty(row, "duration") ?? 0,
            GraphDiagnosticToolSupport.StringProperty(row, "callType"),
            GraphDiagnosticToolSupport.StringProperty(row, "callerNumber"),
            GraphDiagnosticToolSupport.StringProperty(row, "calleeNumber"),
            GraphDiagnosticToolSupport.BoolProperty(row, "successfulCall"),
            GraphDiagnosticToolSupport.StringProperty(row, "operator"),
            GraphDiagnosticToolSupport.DoubleProperty(row, "charge"),
            GraphDiagnosticToolSupport.StringProperty(row, "currency"),
            GraphDiagnosticToolSupport.StringProperty(row, "destinationName"),
            GraphDiagnosticToolSupport.IntProperty(row, "finalSipCode"),
            GraphDiagnosticToolSupport.StringProperty(row, "finalSipCodePhrase"));
}

public sealed record GraphDiagnosticFinding(
    string Severity,
    string Code,
    string What,
    string Why,
    string Fix);

internal static class GraphDiagnosticToolSupport
{
    private static readonly string[] UtcFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
    ];

    public static bool TryTenantContext(
        IReadOnlyDictionary<string, JsonElement> arguments,
        out GraphTenantContext? context)
    {
        var tenantId = arguments["tenantId"].GetString();
        var credentialRef = arguments["credentialRef"].GetString();
        if (!Guid.TryParse(tenantId, out var tenantGuid) || tenantGuid == Guid.Empty ||
            string.IsNullOrWhiteSpace(credentialRef))
        {
            context = null;
            return false;
        }

        context = new GraphTenantContext(tenantGuid, credentialRef);
        return true;
    }

    public static bool TryUtcRange(
        IReadOnlyDictionary<string, JsonElement> arguments,
        TimeSpan maximumRange,
        out DateTimeOffset fromUtc,
        out DateTimeOffset toUtc,
        out string error)
    {
        var fromValid = TryUtc(arguments["fromUtc"].GetString(), out fromUtc);
        var toValid = TryUtc(arguments["toUtc"].GetString(), out toUtc);
        if (!fromValid || !toValid)
        {
            error = "fromUtc and toUtc must be ISO 8601 UTC date-time values ending in Z.";
            return false;
        }

        if (fromUtc >= toUtc)
        {
            error = "fromUtc must be before toUtc.";
            return false;
        }

        if (toUtc - fromUtc > maximumRange)
        {
            error = $"The requested UTC range must not exceed {maximumRange.TotalDays.ToString("0", CultureInfo.InvariantCulture)} days.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string? OptionalString(IReadOnlyDictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? IntProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    public static double? DoubleProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result)
            ? result
            : null;

    public static bool? BoolProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    public static DateTimeOffset? DateProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out var result)
            ? result.ToUniversalTime()
            : null;

    public static bool IsE164(string value) =>
        value.Length is >= 8 and <= 16 &&
        value[0] == '+' &&
        value[1] is >= '1' and <= '9' &&
        value.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool TryUtc(string? value, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(
            value,
            UtcFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
}