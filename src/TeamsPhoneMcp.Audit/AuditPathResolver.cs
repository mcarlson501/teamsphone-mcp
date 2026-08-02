using System.Text;

namespace TeamsPhoneMcp.Audit;

/// <summary>
/// Resolves the on-disk layout of the audit store: one folder per tenant, one
/// JSONL file per day, and a dated snapshot folder alongside (build spec §9.2).
/// </summary>
public sealed class AuditPathResolver
{
    /// <summary>Folder used for records that are not attributable to a tenant (e.g. the sweeper).</summary>
    public const string SystemTenantFolder = "_system";

    public const string SnapshotsFolderName = "snapshots";

    private readonly string _rootPath;

    public AuditPathResolver(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath => _rootPath;

    public string GetTenantDirectory(string tenantId) =>
        Path.Combine(_rootPath, SanitizeSegment(tenantId));

    public string GetDailyFilePath(string tenantId, DateTimeOffset timestamp) =>
        Path.Combine(GetTenantDirectory(tenantId), $"{timestamp.UtcDateTime:yyyy-MM-dd}.jsonl");

    public string GetSnapshotDirectory(string tenantId, DateTimeOffset timestamp) =>
        Path.Combine(
            GetTenantDirectory(tenantId),
            SnapshotsFolderName,
            timestamp.UtcDateTime.ToString("yyyy-MM-dd"));

    /// <summary>
    /// Reduces a caller-supplied identifier to a safe single path segment. Tenant
    /// ids arrive as GUIDs or domain names, but the value still originates from a
    /// client, so path traversal is defended against explicitly.
    /// </summary>
    public static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                    ? character
                    : '_');
        }

        var sanitized = builder.ToString().Trim('.');
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }
}
