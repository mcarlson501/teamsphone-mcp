using Microsoft.Extensions.Options;

namespace TeamsPhoneMcp.Core.Sessions;

public sealed class TenantSessionOptions
{
    public const string SectionName = "TenantSessions";

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public int MaxSessions { get; set; } = 10;

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);
}

internal sealed class TenantSessionOptionsValidator : IValidateOptions<TenantSessionOptions>
{
    public ValidateOptionsResult Validate(string? name, TenantSessionOptions options)
    {
        var failures = new List<string>();

        if (options.IdleTimeout <= TimeSpan.Zero)
        {
            failures.Add("TenantSessions:IdleTimeout must be positive.");
        }

        if (options.MaxSessions <= 0)
        {
            failures.Add("TenantSessions:MaxSessions must be positive.");
        }

        if (options.CleanupInterval <= TimeSpan.Zero)
        {
            failures.Add("TenantSessions:CleanupInterval must be positive.");
        }
        else if (options.IdleTimeout > TimeSpan.Zero && options.CleanupInterval > options.IdleTimeout)
        {
            failures.Add("TenantSessions:CleanupInterval must not exceed IdleTimeout.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}