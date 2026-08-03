using System.Globalization;
using System.Text;

namespace TeamsPhoneMcp.Audit;

public static class AuditReportRenderer
{
    public static string RenderMarkdown(
        string tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyList<AuditRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Teams Phone change history");
        builder.AppendLine();
        builder.AppendLine($"Tenant: `{EscapeMarkdown(tenantId)}`");
        builder.AppendLine($"Period: {fromUtc:O} through {toUtc:O}");
        builder.AppendLine($"Records: {records.Count}");
        builder.AppendLine();
        builder.AppendLine("| Timestamp (UTC) | Tool | Status | Correlation ID | Duration (ms) | Error code |");
        builder.AppendLine("| --- | --- | --- | --- | ---: | --- |");
        foreach (var record in records)
        {
            builder.Append("| ")
                .Append(record.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                .Append(" | ").Append(EscapeMarkdown(record.ToolId))
                .Append(" | ").Append(EscapeMarkdown(record.Status))
                .Append(" | `").Append(EscapeMarkdown(record.CorrelationId)).Append('`')
                .Append(" | ").Append(record.DurationMs.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(EscapeMarkdown(record.ErrorCode ?? string.Empty))
                .AppendLine(" |");
        }

        return builder.ToString();
    }

    public static string RenderCsv(IReadOnlyList<AuditRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("timestampUtc,correlationId,clientId,toolId,toolVersion,status,errorCode,dryRun,simulated,riskTier,durationMs");
        foreach (var record in records)
        {
            builder.Append(Csv(record.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))).Append(',')
                .Append(Csv(record.CorrelationId)).Append(',')
                .Append(Csv(record.ClientId)).Append(',')
                .Append(Csv(record.ToolId)).Append(',')
                .Append(Csv(record.ToolVersion)).Append(',')
                .Append(Csv(record.Status)).Append(',')
                .Append(Csv(record.ErrorCode)).Append(',')
                .Append(record.DryRun.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(record.Simulated.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(record.RiskTier.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(record.DurationMs.ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string EscapeMarkdown(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}