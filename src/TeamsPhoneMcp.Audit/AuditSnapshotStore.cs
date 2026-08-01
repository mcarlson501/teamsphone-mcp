using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TeamsPhoneMcp.Audit;

/// <summary>
/// Stores before/after tenant state as separate artifacts so the JSONL trail
/// stays small and queryable while full diffs remain retrievable (build spec §9.1).
/// </summary>
public interface IAuditSnapshotStore
{
    ValueTask<AuditSnapshotRefs?> StoreAsync(
        string tenantId,
        string correlationId,
        DateTimeOffset timestamp,
        JsonElement? before,
        JsonElement? after,
        CancellationToken cancellationToken = default);
}

/// <summary>No-op store used when auditing is disabled.</summary>
public sealed class NullAuditSnapshotStore : IAuditSnapshotStore
{
    public ValueTask<AuditSnapshotRefs?> StoreAsync(
        string tenantId,
        string correlationId,
        DateTimeOffset timestamp,
        JsonElement? before,
        JsonElement? after,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AuditSnapshotRefs?>(null);
}

/// <summary>
/// Writes snapshots as pretty-printed JSON under the tenant's dated snapshot
/// folder and returns audit-root-relative references.
/// </summary>
public sealed class FileAuditSnapshotStore : IAuditSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly AuditPathResolver _paths;
    private readonly IOptionsMonitor<AuditOptions> _options;
    private readonly ILogger<FileAuditSnapshotStore> _logger;

    public FileAuditSnapshotStore(
        AuditPathResolver paths,
        IOptionsMonitor<AuditOptions> options,
        ILogger<FileAuditSnapshotStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _options = options;
        _logger = logger;
    }

    public async ValueTask<AuditSnapshotRefs?> StoreAsync(
        string tenantId,
        string correlationId,
        DateTimeOffset timestamp,
        JsonElement? before,
        JsonElement? after,
        CancellationToken cancellationToken = default)
    {
        if (!_options.CurrentValue.Enabled || (before is null && after is null))
        {
            return null;
        }

        var directory = _paths.GetSnapshotDirectory(tenantId, timestamp);
        var id = AuditPathResolver.SanitizeSegment(correlationId);

        try
        {
            Directory.CreateDirectory(directory);
            var beforeRef = await WriteAsync(directory, id, "before", before, cancellationToken).ConfigureAwait(false);
            var afterRef = await WriteAsync(directory, id, "after", after, cancellationToken).ConfigureAwait(false);

            return beforeRef is null && afterRef is null
                ? null
                : new AuditSnapshotRefs(beforeRef, afterRef);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to store audit snapshots for correlation {CorrelationId}.", correlationId);
            return null;
        }
    }

    private async ValueTask<string?> WriteAsync(
        string directory,
        string correlationId,
        string kind,
        JsonElement? state,
        CancellationToken cancellationToken)
    {
        if (state is null)
        {
            return null;
        }

        var fileName = $"{correlationId}-{kind}.json";
        var path = Path.Combine(directory, fileName);
        var json = JsonSerializer.Serialize(state.Value, SerializerOptions);

        await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        return Path.GetRelativePath(_paths.RootPath, path).Replace(Path.DirectorySeparatorChar, '/');
    }
}
