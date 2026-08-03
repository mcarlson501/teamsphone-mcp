using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using TeamsPhoneMcp.Core.Tools;
using TeamsPhoneMcp.Host;

namespace TeamsPhoneMcp.UnitTests;

public sealed class GraphDiagnosticsAcceptanceTests
{
    private const string BearerToken = "graph-diagnostics-token";
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetPstnUsage_MergesFiltersAndSummarizesBothSources()
    {
        var fake = new FakeGraphCallRecordsClient
        {
            PstnData = new GraphPstnCallData(
                [
                    Json(new
                    {
                        id = "pstn-1",
                        userPrincipalName = "user@example.com",
                        startDateTime = "2026-01-30T10:00:00Z",
                        endDateTime = "2026-01-30T10:02:00Z",
                        duration = 120,
                        callType = "user_out",
                        callerNumber = "+15550000001",
                        calleeNumber = "+15550000002",
                        @operator = "Microsoft",
                        charge = 1.25,
                        currency = "USD",
                    }),
                    Json(new
                    {
                        id = "pstn-other",
                        userPrincipalName = "other@example.com",
                        startDateTime = "2026-01-30T11:00:00Z",
                        duration = 60,
                    }),
                ],
                [
                    Json(new
                    {
                        id = "dr-1",
                        userPrincipalName = "user@example.com",
                        startDateTime = "2026-01-30T12:00:00Z",
                        duration = 30,
                        callType = "ByotOut",
                        callerNumber = "+15550000001",
                        calleeNumber = "+15550000003",
                        successfulCall = false,
                        finalSipCode = 503,
                        finalSipCodePhrase = "Service Unavailable",
                    }),
                ],
                Truncated: false),
        };
        await using var factory = CreateFactory(fake);
        await using var client = await CreateClientAsync(factory);

        var result = await client.CallToolAsync(
            "get-pstn-usage",
            Arguments(
                ("fromUtc", "2026-01-01T00:00:00Z"),
                ("toUtc", "2026-02-01T00:00:00Z"),
                ("userUpn", "user@example.com")));

        Assert.NotEqual(true, result.IsError);
        var payload = result.StructuredContent!.Value;
        Assert.Equal(2, payload.GetProperty("calls").GetArrayLength());
        Assert.Equal(2, payload.GetProperty("totals").GetProperty("totalCalls").GetInt32());
        Assert.Equal(150, payload.GetProperty("totals").GetProperty("durationSeconds").GetInt64());
        Assert.Equal(1, payload.GetProperty("totals").GetProperty("failedCalls").GetInt32());
        Assert.Contains(
            payload.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("code").GetString() == "directRoutingFailures");
        Assert.Equal(1, fake.PstnRequests);
    }

    [Fact]
    public async Task GetCallQualitySummary_AggregatesStreamsAndReturnsActionableFindings()
    {
        var fake = new FakeGraphCallRecordsClient
        {
            QualityData = new GraphQualityCallData(
                "user-id",
                [
                    Json(new
                    {
                        id = "call-1",
                        startDateTime = "2026-01-30T10:00:00Z",
                        endDateTime = "2026-01-30T10:10:00Z",
                        type = "peerToPeer",
                        sessions = new[]
                        {
                            new
                            {
                                segments = new[]
                                {
                                    new
                                    {
                                        media = new[]
                                        {
                                            new
                                            {
                                                streams = new[]
                                                {
                                                    new
                                                    {
                                                        averagePacketLossRate = 0.03,
                                                        maxPacketLossRate = 0.12,
                                                        averageJitter = "PT0.040S",
                                                        maxJitter = "PT0.120S",
                                                        averageRoundTripTime = "PT0.350S",
                                                        maxRoundTripTime = "PT0.900S",
                                                        averageAudioDegradation = 1.2,
                                                        averageRatioOfConcealedSamples = 0.04,
                                                    },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    }),
                ],
                Truncated: false),
        };
        await using var factory = CreateFactory(fake);
        await using var client = await CreateClientAsync(factory);

        var result = await client.CallToolAsync(
            "get-call-quality-summary",
            Arguments(
                ("userUpn", "user@example.com"),
                ("fromUtc", "2026-01-29T00:00:00Z"),
                ("toUtc", "2026-01-31T00:00:00Z")));

        Assert.NotEqual(true, result.IsError);
        var payload = result.StructuredContent!.Value;
        Assert.Equal("issuesFound", payload.GetProperty("qualityStatus").GetString());
        Assert.Equal(3, payload.GetProperty("metrics").GetProperty("averagePacketLossPercent").GetDouble());
        Assert.Equal(120, payload.GetProperty("metrics").GetProperty("maxJitterMs").GetDouble());
        Assert.Contains(
            payload.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("code").GetString() == "packetLossHigh");
        Assert.Contains(
            payload.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("code").GetString() == "roundTripTimeHigh");
        Assert.Equal(1, fake.QualityRequests);
    }

    [Fact]
    public async Task GraphDiagnostics_RejectInvalidOrExpiredRangesBeforeGraphAccess()
    {
        var fake = new FakeGraphCallRecordsClient();
        await using var factory = CreateFactory(fake);
        await using var client = await CreateClientAsync(factory);

        var usage = await client.CallToolAsync(
            "get-pstn-usage",
            Arguments(
                ("fromUtc", "2025-01-01T00:00:00Z"),
                ("toUtc", "2025-04-02T00:00:00Z")));
        Assert.True(usage.IsError);
        Assert.Equal("invalidDateRange", usage.StructuredContent!.Value.GetProperty("errorCode").GetString());

        var quality = await client.CallToolAsync(
            "get-call-quality-summary",
            Arguments(
                ("userUpn", "user@example.com"),
                ("fromUtc", "2026-01-01T00:00:00Z"),
                ("toUtc", "2026-01-02T00:00:00Z")));
        Assert.True(quality.IsError);
        Assert.Equal("callRecordsExpired", quality.StructuredContent!.Value.GetProperty("errorCode").GetString());
        Assert.Equal(0, fake.PstnRequests);
        Assert.Equal(0, fake.QualityRequests);
    }

    [Fact]
    public async Task GraphDiagnostics_ReturnSafeStableGraphErrors()
    {
        var fake = new FakeGraphCallRecordsClient
        {
            Failure = new GraphCallRecordsException(
                "callRecordsPermissionDenied",
                "The Entra application requires the CallRecords.Read.All application permission with admin consent."),
        };
        await using var factory = CreateFactory(fake);
        await using var client = await CreateClientAsync(factory);

        var result = await client.CallToolAsync(
            "get-pstn-usage",
            Arguments(
                ("fromUtc", "2026-01-01T00:00:00Z"),
                ("toUtc", "2026-02-01T00:00:00Z")));

        Assert.True(result.IsError);
        var payload = result.StructuredContent!.Value;
        Assert.Equal("callRecordsPermissionDenied", payload.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("credential-ref", payload.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task GraphDiagnostics_RejectInvalidPhoneNumbersAndExplainTruncation()
    {
        var fake = new FakeGraphCallRecordsClient
        {
            PstnData = new GraphPstnCallData([], [], Truncated: true),
            QualityData = new GraphQualityCallData("user-id", [], Truncated: true),
        };
        await using var factory = CreateFactory(fake);
        await using var client = await CreateClientAsync(factory);

        var invalidNumber = await client.CallToolAsync(
            "get-pstn-usage",
            Arguments(
                ("fromUtc", "2026-01-01T00:00:00Z"),
                ("toUtc", "2026-02-01T00:00:00Z"),
                ("phoneNumber", "+1-555-000-0001")));
        Assert.True(invalidNumber.IsError);
        Assert.Equal(
            "invalidPhoneNumber",
            invalidNumber.StructuredContent!.Value.GetProperty("errorCode").GetString());
        Assert.Equal(0, fake.PstnRequests);

        var usage = await client.CallToolAsync(
            "get-pstn-usage",
            Arguments(
                ("fromUtc", "2026-01-01T00:00:00Z"),
                ("toUtc", "2026-02-01T00:00:00Z")));
        Assert.Contains(
            usage.StructuredContent!.Value.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("code").GetString() == "resultsTruncated");

        var quality = await client.CallToolAsync(
            "get-call-quality-summary",
            Arguments(
                ("userUpn", "user@example.com"),
                ("fromUtc", "2026-01-29T00:00:00Z"),
                ("toUtc", "2026-01-31T00:00:00Z")));
        Assert.Contains(
            quality.StructuredContent!.Value.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("code").GetString() == "resultsTruncated");
    }

    private static WebApplicationFactory<Program> CreateFactory(IGraphCallRecordsClient client) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TEAMSPHONE_MCP_BEARER_TOKEN", BearerToken);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGraphCallRecordsClient>();
                services.AddSingleton(client);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            });
        });

    private static async Task<McpClient> CreateClientAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static Dictionary<string, object?> Arguments(params (string Name, object? Value)[] values)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["tenantId"] = TenantId,
            ["credentialRef"] = "credential-ref",
        };
        foreach (var (name, value) in values)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeGraphCallRecordsClient : IGraphCallRecordsClient
    {
        public GraphPstnCallData PstnData { get; init; } = new([], [], false);

        public GraphQualityCallData QualityData { get; init; } = new("user-id", [], false);

        public GraphCallRecordsException? Failure { get; init; }

        public int PstnRequests { get; private set; }

        public int QualityRequests { get; private set; }

        public Task<GraphPstnCallData> GetPstnCallsAsync(
            GraphTenantContext context,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
        {
            PstnRequests++;
            return Failure is null ? Task.FromResult(PstnData) : Task.FromException<GraphPstnCallData>(Failure);
        }

        public Task<GraphQualityCallData> GetQualityCallsAsync(
            GraphTenantContext context,
            string userPrincipalName,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
        {
            QualityRequests++;
            return Failure is null ? Task.FromResult(QualityData) : Task.FromException<GraphQualityCallData>(Failure);
        }
    }
}