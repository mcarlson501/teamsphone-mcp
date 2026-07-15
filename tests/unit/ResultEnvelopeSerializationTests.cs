using System.Text.Json;
using TeamsPhoneMcp.Core.Execution;

namespace TeamsPhoneMcp.UnitTests;

public class ResultEnvelopeSerializationTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000009");

    [Fact]
    public void Status_SerializesAsString_NotInteger()
    {
        var envelope = Minimal(ToolExecutionStatus.DryRunCompleted);

        var json = JsonSerializer.Serialize(envelope);

        Assert.Contains("\"Status\":\"DryRunCompleted\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Status\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvelopeVersion_DefaultsToOne()
    {
        var envelope = Minimal(ToolExecutionStatus.Succeeded);

        Assert.Equal(1, envelope.EnvelopeVersion);
    }

    [Fact]
    public void FullyPopulatedEnvelope_RoundTrips()
    {
        var before = JsonSerializer.SerializeToElement(new { callerId = "A" });
        var after = JsonSerializer.SerializeToElement(new { callerId = "B" });
        var envelope = new ToolResultEnvelope
        {
            Status = ToolExecutionStatus.Succeeded,
            ToolId = "move-number",
            ToolVersion = "1.2.3",
            TenantId = TenantId,
            CorrelationId = "corr-42",
            DryRun = false,
            ConfirmationToken = null,
            Summary = "Moved caller ID.",
            Diff = new ToolDiff(before, after),
            Preflight = [new ToolCheckResult("hasNumber", true, null)],
            Verification = [new ToolCheckResult("moved", true, "confirmed")],
            Timings = new ToolTimings(120, new Dictionary<string, long> { ["Execute"] = 100 }),
            Error = null
        };

        var json = JsonSerializer.Serialize(envelope);
        var roundTripped = JsonSerializer.Deserialize<ToolResultEnvelope>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(ToolExecutionStatus.Succeeded, roundTripped!.Status);
        Assert.Equal("move-number", roundTripped.ToolId);
        Assert.Equal(TenantId, roundTripped.TenantId);
        Assert.Equal("A", roundTripped.Diff!.Before!.Value.GetProperty("callerId").GetString());
        Assert.Equal("B", roundTripped.Diff!.After!.Value.GetProperty("callerId").GetString());
        Assert.Single(roundTripped.Preflight!);
        Assert.Equal(120, roundTripped.Timings!.TotalMs);
        Assert.Equal(100, roundTripped.Timings!.Stages["Execute"]);
    }

    private static ToolResultEnvelope Minimal(ToolExecutionStatus status) => new()
    {
        Status = status,
        ToolId = "tool",
        ToolVersion = "1.0.0",
        TenantId = TenantId,
        CorrelationId = "corr",
        DryRun = status == ToolExecutionStatus.DryRunCompleted,
        Summary = "summary"
    };
}
