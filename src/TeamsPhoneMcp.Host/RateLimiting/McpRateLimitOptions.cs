namespace TeamsPhoneMcp.Host.RateLimiting;

public sealed class McpRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public int PermitLimit { get; set; } = 30;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}