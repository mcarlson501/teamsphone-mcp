using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TeamsPhoneMcp.Audit;

/// <summary>Destination for audit records. Implementations must never throw at the call site.</summary>
public interface IAuditSink
{
    ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default);
}

/// <summary>Used when auditing is disabled, and as the fail-safe default in unit tests.</summary>
public sealed class NullAuditSink : IAuditSink
{
    public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

/// <summary>
/// Append-only JSONL writer, one file per tenant per day (build spec §9.2).
/// Plain files on purpose: greppable, <c>jq</c>-able and backup-friendly with no
/// infrastructure. A failed write degrades to a log warning — losing the trail is
/// bad, but failing a tenant operation because the disk hiccuped is worse.
/// </summary>
public sealed class JsonlAuditSink : IAuditSink
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.Ordinal);
    private readonly AuditPathResolver _paths;
    private readonly IOptionsMonitor<AuditOptions> _options;
    private readonly ILogger<JsonlAuditSink> _logger;

    public JsonlAuditSink(
        AuditPathResolver paths,
        IOptionsMonitor<AuditOptions> options,
        ILogger<JsonlAuditSink> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _options = options;
        _logger = logger;
    }

    public async ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        var path = _paths.GetDailyFilePath(record.TenantId, record.Timestamp);
        var gate = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var line = JsonSerializer.Serialize(record, SerializerOptions) + Environment.NewLine;

            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(
                ex,
                "Failed to write the audit record for correlation {CorrelationId}.",
                record.CorrelationId);
        }
        finally
        {
            gate.Release();
        }
    }
}
