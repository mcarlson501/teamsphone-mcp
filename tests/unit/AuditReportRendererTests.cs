using TeamsPhoneMcp.Audit;

namespace TeamsPhoneMcp.UnitTests;

public sealed class AuditReportRendererTests
{
    [Fact]
    public void RenderMarkdown_ProducesCustomerReadableChangeTableWithoutRawErrors()
    {
        var record = CreateRecord() with { ErrorCode = "verifyFailed", ErrorMessage = "raw tenant detail" };

        var report = AuditReportRenderer.RenderMarkdown(TenantId, FromUtc, ToUtc, [record]);

        Assert.Contains("# Teams Phone change history", report);
        Assert.Contains("move-number-between-users", report);
        Assert.Contains("verifyFailed", report);
        Assert.DoesNotContain("raw tenant detail", report);
    }

    [Fact]
    public void RenderCsv_QuotesFieldsAndOmitsRawErrors()
    {
        var record = CreateRecord() with { ClientId = "agent,version-1", ErrorMessage = "raw tenant detail" };

        var report = AuditReportRenderer.RenderCsv([record]);

        Assert.Contains("\"agent,version-1\"", report);
        Assert.Contains("move-number-between-users", report);
        Assert.DoesNotContain("raw tenant detail", report);
    }

    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset FromUtc = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ToUtc = FromUtc.AddDays(1);

    private static AuditRecord CreateRecord() =>
        new()
        {
            Timestamp = FromUtc.AddHours(1),
            CorrelationId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            ClientId = "inspector/1.0",
            TenantId = TenantId,
            ToolId = "move-number-between-users",
            ToolVersion = "1.0.0",
            Status = "Succeeded",
            DurationMs = 42,
        };
}