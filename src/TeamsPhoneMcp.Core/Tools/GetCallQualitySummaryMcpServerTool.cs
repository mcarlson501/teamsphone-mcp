using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeamsPhoneMcp.Core.Manifests;

namespace TeamsPhoneMcp.Core.Tools;

public sealed class GetCallQualitySummaryMcpServerTool(ToolManifest manifest) : McpServerTool
{
    public const string ToolId = "get-call-quality-summary";

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
            return ToCallToolResult(CallQualitySummaryResult.Failure(
                manifest,
                tenantId,
                correlationId,
                "invalidTenant",
                "tenantId must be a non-empty GUID."));
        }

        if (!GraphDiagnosticToolSupport.TryUtcRange(
                arguments,
                TimeSpan.FromDays(30),
                out var fromUtc,
                out var toUtc,
                out var rangeError))
        {
            return ToCallToolResult(CallQualitySummaryResult.Failure(
                manifest,
                tenantId,
                correlationId,
                "invalidDateRange",
                rangeError));
        }

        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        if (fromUtc < now.AddDays(-30))
        {
            return ToCallToolResult(CallQualitySummaryResult.Failure(
                manifest,
                tenantId,
                correlationId,
                "callRecordsExpired",
                "Detailed Microsoft Graph call records are retained for 30 days; choose a newer fromUtc value."));
        }

        var userUpn = arguments["userUpn"].GetString()!;
        try
        {
            var data = await services.GetRequiredService<IGraphCallRecordsClient>()
                .GetQualityCallsAsync(context!, userUpn, fromUtc, toUtc, cancellationToken)
                .ConfigureAwait(false);
            var calls = data.Calls.Select(CallQualityDetail.FromGraph).ToArray();
            var metrics = CallQualityMetrics.FromStreams(calls.SelectMany(call => call.QualityStreams).ToArray());
            var findings = BuildFindings(metrics, calls.Length).ToList();
            if (data.Truncated)
            {
                findings.Add(new GraphDiagnosticFinding(
                    "warning",
                    "resultsTruncated",
                    "The safety limit was reached before all call records or sessions were processed.",
                    "Metrics and findings may not represent the entire requested window.",
                    "Retry with a narrower UTC date range; Graph paging links are intentionally not exposed."));
            }

            var status = calls.Length == 0 || metrics.StreamCount == 0
                ? "noData"
                : findings.Count == 0 ? "healthy" : "issuesFound";
            var result = new CallQualitySummaryResult(
                "succeeded",
                manifest.Id,
                manifest.Version,
                tenantId,
                correlationId,
                calls.Length == 0
                    ? $"No completed call records were found for {userUpn} in the selected window."
                    : $"Analyzed {metrics.StreamCount} media stream(s) across {calls.Length} call(s) for {userUpn}; found {findings.Count} issue(s){(data.Truncated ? "; results are truncated" : string.Empty)}.",
                userUpn,
                fromUtc,
                toUtc,
                status,
                calls,
                metrics,
                findings,
                data.Truncated,
                null,
                null);
            return ToCallToolResult(result);
        }
        catch (GraphCallRecordsException ex)
        {
            return ToCallToolResult(CallQualitySummaryResult.Failure(
                manifest,
                tenantId,
                correlationId,
                ex.ErrorCode,
                ex.Message));
        }
    }

    private static IReadOnlyList<GraphDiagnosticFinding> BuildFindings(CallQualityMetrics metrics, int callCount)
    {
        if (callCount == 0)
        {
            return [];
        }

        if (metrics.StreamCount == 0)
        {
            return
            [
                new GraphDiagnosticFinding(
                    "warning",
                    "qualityDataUnavailable",
                    "Call records were found, but they contained no media-stream quality metrics.",
                    "Very recent calls can still be processing, and some call paths do not report every metric.",
                    "Wait for call-record processing to finish, then retry with a completed call window.")
            ];
        }

        var findings = new List<GraphDiagnosticFinding>();
        if (metrics.AveragePacketLossPercent > 1 || metrics.MaxPacketLossPercent > 5)
        {
            findings.Add(new GraphDiagnosticFinding(
                metrics.MaxPacketLossPercent > 10 ? "critical" : "warning",
                "packetLossHigh",
                $"Packet loss averaged {metrics.AveragePacketLossPercent:0.##}% and peaked at {metrics.MaxPacketLossPercent:0.##}%.",
                "Packet loss causes clipped, robotic, or missing audio.",
                "Check Wi-Fi signal, wired-network errors, congestion, and QoS treatment on the affected path."));
        }

        if (metrics.AverageJitterMs > 30 || metrics.MaxJitterMs > 50)
        {
            findings.Add(new GraphDiagnosticFinding(
                metrics.MaxJitterMs > 100 ? "critical" : "warning",
                "jitterHigh",
                $"Jitter averaged {metrics.AverageJitterMs:0.##} ms and peaked at {metrics.MaxJitterMs:0.##} ms.",
                "Variable packet arrival times can produce choppy audio and stress jitter buffers.",
                "Check network congestion, unstable Wi-Fi, and QoS consistency between the client and Teams media edge."));
        }

        if (metrics.AverageRoundTripTimeMs > 300 || metrics.MaxRoundTripTimeMs > 500)
        {
            findings.Add(new GraphDiagnosticFinding(
                metrics.MaxRoundTripTimeMs > 800 ? "critical" : "warning",
                "roundTripTimeHigh",
                $"Round-trip time averaged {metrics.AverageRoundTripTimeMs:0.##} ms and peaked at {metrics.MaxRoundTripTimeMs:0.##} ms.",
                "High latency creates conversational delay and talk-over.",
                "Check VPN hairpinning, WAN routing, proxy bypass, and the client's distance from the selected Teams media edge."));
        }

        if (metrics.AverageAudioDegradation > 1)
        {
            findings.Add(new GraphDiagnosticFinding(
                "warning",
                "audioDegradationHigh",
                $"Average network audio degradation was {metrics.AverageAudioDegradation:0.##} MOS points.",
                "The combined impact of network loss and jitter materially reduced perceived audio quality.",
                "Prioritize the packet-loss and jitter remediation for the affected network path."));
        }

        if (metrics.AverageConcealedSamplesPercent > 2)
        {
            findings.Add(new GraphDiagnosticFinding(
                "warning",
                "concealedSamplesHigh",
                $"Audio concealment averaged {metrics.AverageConcealedSamplesPercent:0.##}% of samples.",
                "The client had to synthesize missing audio because media packets were unavailable.",
                "Investigate packet loss, jitter, Wi-Fi roaming, and endpoint CPU or driver health."));
        }

        return findings;
    }

    private static CallToolResult ToCallToolResult(CallQualitySummaryResult result) =>
        LocalAuditToolSupport.ToCallToolResult(result, result.Summary, result.ErrorCode is not null);
}

public sealed record CallQualitySummaryResult(
    string Status,
    string ToolId,
    string ToolVersion,
    string TenantId,
    string CorrelationId,
    string Summary,
    string? UserPrincipalName,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    string? QualityStatus,
    IReadOnlyList<CallQualityDetail> Calls,
    CallQualityMetrics? Metrics,
    IReadOnlyList<GraphDiagnosticFinding> Findings,
    bool Truncated,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static CallQualitySummaryResult Failure(
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
            null,
            null,
            [],
            null,
            [],
            false,
            errorCode,
            message);
}

public sealed record CallQualityDetail(
    string? Id,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime,
    string? CallType,
    int SessionCount,
    CallQualityMetrics Metrics)
{
    [JsonIgnore]
    public IReadOnlyList<JsonElement> QualityStreams { get; init; } = [];

    public static CallQualityDetail FromGraph(JsonElement call)
    {
        var streams = new List<JsonElement>();
        var sessionCount = 0;
        if (call.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array)
        {
            foreach (var session in sessions.EnumerateArray())
            {
                sessionCount++;
                if (!session.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var segment in segments.EnumerateArray())
                {
                    if (!segment.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var medium in media.EnumerateArray())
                    {
                        if (medium.TryGetProperty("streams", out var mediumStreams) &&
                            mediumStreams.ValueKind == JsonValueKind.Array)
                        {
                            streams.AddRange(mediumStreams.EnumerateArray().Select(stream => stream.Clone()));
                        }
                    }
                }
            }
        }

        return new CallQualityDetail(
            GraphDiagnosticToolSupport.StringProperty(call, "id"),
            GraphDiagnosticToolSupport.DateProperty(call, "startDateTime"),
            GraphDiagnosticToolSupport.DateProperty(call, "endDateTime"),
            GraphDiagnosticToolSupport.StringProperty(call, "type"),
            sessionCount,
            CallQualityMetrics.FromStreams(streams))
        {
            QualityStreams = streams,
        };
    }
}

public sealed record CallQualityMetrics(
    int StreamCount,
    double AveragePacketLossPercent,
    double MaxPacketLossPercent,
    double AverageJitterMs,
    double MaxJitterMs,
    double AverageRoundTripTimeMs,
    double MaxRoundTripTimeMs,
    double AverageAudioDegradation,
    double AverageConcealedSamplesPercent)
{
    public static CallQualityMetrics FromStreams(IReadOnlyList<JsonElement> streams)
    {
        var metricStreams = streams.Where(HasAudioQualityMetric).ToArray();
        var averagePacketLoss = Values(metricStreams, "averagePacketLossRate").ToArray();
        var maxPacketLoss = Values(metricStreams, "maxPacketLossRate").Concat(averagePacketLoss).ToArray();
        var averageJitter = Durations(metricStreams, "averageJitter").ToArray();
        var maxJitter = Durations(metricStreams, "maxJitter").Concat(averageJitter).ToArray();
        var averageRoundTrip = Durations(metricStreams, "averageRoundTripTime").ToArray();
        var maxRoundTrip = Durations(metricStreams, "maxRoundTripTime").Concat(averageRoundTrip).ToArray();
        var degradation = Values(metricStreams, "averageAudioDegradation").ToArray();
        var concealed = Values(metricStreams, "averageRatioOfConcealedSamples").ToArray();
        return new CallQualityMetrics(
            metricStreams.Length,
            Average(averagePacketLoss) * 100,
            Maximum(maxPacketLoss) * 100,
            Average(averageJitter),
            Maximum(maxJitter),
            Average(averageRoundTrip),
            Maximum(maxRoundTrip),
            Average(degradation),
            Average(concealed) * 100);
    }

    private static IEnumerable<double> Values(IEnumerable<JsonElement> streams, string name) =>
        streams.Select(stream => GraphDiagnosticToolSupport.DoubleProperty(stream, name)).OfType<double>();

    private static bool HasAudioQualityMetric(JsonElement stream) =>
        GraphDiagnosticToolSupport.DoubleProperty(stream, "averagePacketLossRate") is not null ||
        GraphDiagnosticToolSupport.DoubleProperty(stream, "maxPacketLossRate") is not null ||
        GraphDiagnosticToolSupport.StringProperty(stream, "averageJitter") is not null ||
        GraphDiagnosticToolSupport.StringProperty(stream, "maxJitter") is not null ||
        GraphDiagnosticToolSupport.StringProperty(stream, "averageRoundTripTime") is not null ||
        GraphDiagnosticToolSupport.StringProperty(stream, "maxRoundTripTime") is not null ||
        GraphDiagnosticToolSupport.DoubleProperty(stream, "averageAudioDegradation") is not null ||
        GraphDiagnosticToolSupport.DoubleProperty(stream, "averageRatioOfConcealedSamples") is not null;

    private static IEnumerable<double> Durations(IEnumerable<JsonElement> streams, string name)
    {
        foreach (var stream in streams)
        {
            var value = GraphDiagnosticToolSupport.StringProperty(stream, name);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            double milliseconds;
            try
            {
                milliseconds = XmlConvert.ToTimeSpan(value).TotalMilliseconds;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                // Ignore a malformed individual metric while preserving the rest of the call record.
                continue;
            }

            yield return milliseconds;
        }
    }

    private static double Average(IReadOnlyCollection<double> values) =>
        values.Count == 0 ? 0 : values.Average();

    private static double Maximum(IReadOnlyCollection<double> values) =>
        values.Count == 0 ? 0 : values.Max();

}