using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TeamsPhoneMcp.Audit;

/// <summary>Outcome of one retention pass.</summary>
public sealed record AuditSweepResult(int DeletedFiles, int DeletedSnapshotFolders, int RetentionDays);

/// <summary>
/// Prunes audit files past the configured retention window and records the
/// pruning itself, so a gap in the trail is always explained (build spec §9.2).
/// Expiry is decided from the date encoded in the file or folder name rather
/// than the filesystem timestamp, which survives backup and restore.
/// </summary>
public sealed class AuditRetentionSweeper
{
    private const string DateFormat = "yyyy-MM-dd";

    private readonly AuditPathResolver _paths;
    private readonly IAuditSink _sink;
    private readonly IOptionsMonitor<AuditOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditRetentionSweeper> _logger;

    public AuditRetentionSweeper(
        AuditPathResolver paths,
        IAuditSink sink,
        IOptionsMonitor<AuditOptions> options,
        TimeProvider timeProvider,
        ILogger<AuditRetentionSweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _sink = sink;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<AuditSweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        var now = _timeProvider.GetUtcNow();
        var result = new AuditSweepResult(0, 0, options.RetentionDays);

        if (!options.Enabled || !Directory.Exists(_paths.RootPath))
        {
            return result;
        }

        var cutoff = now.UtcDateTime.Date.AddDays(-options.RetentionDays);
        var deletedFiles = 0;
        var deletedFolders = 0;

        foreach (var tenantDirectory in Directory.EnumerateDirectories(_paths.RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var file in Directory.EnumerateFiles(tenantDirectory, "*.jsonl"))
            {
                if (IsExpired(Path.GetFileNameWithoutExtension(file), cutoff) && TryDelete(file))
                {
                    deletedFiles++;
                }
            }

            var snapshotsRoot = Path.Combine(tenantDirectory, AuditPathResolver.SnapshotsFolderName);
            if (!Directory.Exists(snapshotsRoot))
            {
                continue;
            }

            foreach (var snapshotDirectory in Directory.EnumerateDirectories(snapshotsRoot))
            {
                if (IsExpired(Path.GetFileName(snapshotDirectory), cutoff) && TryDeleteDirectory(snapshotDirectory))
                {
                    deletedFolders++;
                }
            }
        }

        result = result with { DeletedFiles = deletedFiles, DeletedSnapshotFolders = deletedFolders };

        if (deletedFiles > 0 || deletedFolders > 0)
        {
            _logger.LogInformation(
                "Audit retention sweep removed {DeletedFiles} daily files and {DeletedFolders} snapshot folders older than {RetentionDays} days.",
                deletedFiles,
                deletedFolders,
                options.RetentionDays);

            await RecordSweepAsync(result, now, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task RecordSweepAsync(AuditSweepResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = new AuditRecord
        {
            Timestamp = now,
            CorrelationId = Guid.NewGuid().ToString(),
            TenantId = AuditPathResolver.SystemTenantFolder,
            ToolId = "audit-retention-sweep",
            ToolVersion = "1.0.0",
            Status = "Succeeded",
            Parameters = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                retentionDays = result.RetentionDays,
                deletedFiles = result.DeletedFiles,
                deletedSnapshotFolders = result.DeletedSnapshotFolders,
            }),
        };

        await _sink.WriteAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsExpired(string name, DateTime cutoff) =>
        DateTime.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
        date < cutoff;

    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to prune the expired audit file {AuditFile}.", path);
            return false;
        }
    }

    private bool TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to prune the expired snapshot folder {SnapshotFolder}.", path);
            return false;
        }
    }
}
