using Microsoft.Extensions.Options;

namespace TeamsPhoneMcp.Audit;

/// <summary>
/// Operator-controlled audit settings (build spec §9.2). Defaults are chosen so
/// a self-hoster gets a working, greppable audit trail with zero configuration.
/// </summary>
public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>Turning this off disables audit writing entirely; the sweeper stops too.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Root directory for the append-only JSONL files and snapshot artifacts.
    /// Relative paths resolve against the process content root.
    /// </summary>
    public string RootPath { get; set; } = "audit";

    /// <summary>Days an audit file and its snapshots are retained before the sweeper prunes them.</summary>
    public int RetentionDays { get; set; } = 400;

    /// <summary>How often the retention sweeper runs.</summary>
    public int SweepIntervalHours { get; set; } = 24;
}

/// <summary>Fails startup on nonsensical audit settings rather than silently losing evidence.</summary>
public sealed class AuditOptionsValidator : IValidateOptions<AuditOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            failures.Add($"{AuditOptions.SectionName}:{nameof(AuditOptions.RootPath)} must not be empty.");
        }

        if (options.RetentionDays < 1)
        {
            failures.Add($"{AuditOptions.SectionName}:{nameof(AuditOptions.RetentionDays)} must be at least 1.");
        }

        if (options.SweepIntervalHours < 1)
        {
            failures.Add($"{AuditOptions.SectionName}:{nameof(AuditOptions.SweepIntervalHours)} must be at least 1.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
