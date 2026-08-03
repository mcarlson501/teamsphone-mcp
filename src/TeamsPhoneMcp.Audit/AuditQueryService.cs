using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TeamsPhoneMcp.Audit;

public sealed record AuditQuery(string TenantId)
{
    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    public string? ToolId { get; init; }

    public string? Status { get; init; }

    public string? ClientId { get; init; }

    public string? CorrelationId { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; } = 100;
}

public sealed record AuditQueryPage(
    IReadOnlyList<AuditRecord> Records,
    int Offset,
    int TotalCount,
    bool HasMore);

public sealed record AuditChangeDetail(
    AuditRecord Record,
    JsonElement? Before,
    JsonElement? After);

public interface IAuditQueryService
{
    Task<AuditQueryPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default);

    Task<AuditChangeDetail?> GetChangeDetailAsync(
        string tenantId,
        string correlationId,
        CancellationToken cancellationToken = default);
}

public sealed class NullAuditQueryService : IAuditQueryService
{
    public Task<AuditQueryPage> QueryAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult(new AuditQueryPage([], query.Offset, 0, HasMore: false));
    }

    public Task<AuditChangeDetail?> GetChangeDetailAsync(
        string tenantId,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<AuditChangeDetail?>(null);
}

public sealed class FileAuditQueryService(
    AuditPathResolver paths,
    ILogger<FileAuditQueryService> logger) : IAuditQueryService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<AuditQueryPage> QueryAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.TenantId);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);

        if (query.FromUtc > query.ToUtc)
        {
            throw new ArgumentException("The audit query start must not be after its end.", nameof(query));
        }

        var tenantDirectory = paths.GetTenantDirectory(query.TenantId);
        if (!Directory.Exists(tenantDirectory))
        {
            return new AuditQueryPage([], query.Offset, 0, HasMore: false);
        }

        var records = new List<AuditRecord>();
        foreach (var file in Directory.EnumerateFiles(tenantDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            await foreach (var line in File.ReadLinesAsync(file, cancellationToken))
            {
                AuditRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<AuditRecord>(line, SerializerOptions);
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "Skipped a malformed audit record in {AuditFile}.", file);
                    continue;
                }

                if (record is not null && Matches(record, query))
                {
                    records.Add(record);
                }
            }
        }

        records.Sort(static (left, right) => right.Timestamp.CompareTo(left.Timestamp));
        var page = records.Skip(query.Offset).Take(query.Limit).ToArray();

        return new AuditQueryPage(
            page,
            query.Offset,
            records.Count,
            HasMore: query.Offset + page.Length < records.Count);
    }

    public async Task<AuditChangeDetail?> GetChangeDetailAsync(
        string tenantId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var page = await QueryAsync(
            new AuditQuery(tenantId) { CorrelationId = correlationId, Limit = 1 },
            cancellationToken).ConfigureAwait(false);
        var record = page.Records.SingleOrDefault();
        if (record is null)
        {
            return null;
        }

        var before = await ReadSnapshotAsync(
            tenantId,
            record.SnapshotRefs?.Before,
            cancellationToken).ConfigureAwait(false);
        var after = await ReadSnapshotAsync(
            tenantId,
            record.SnapshotRefs?.After,
            cancellationToken).ConfigureAwait(false);
        return new AuditChangeDetail(record, before, after);
    }

    private static bool Matches(AuditRecord record, AuditQuery query) =>
        string.Equals(record.TenantId, query.TenantId, StringComparison.OrdinalIgnoreCase) &&
        (!query.FromUtc.HasValue || record.Timestamp >= query.FromUtc.Value) &&
        (!query.ToUtc.HasValue || record.Timestamp <= query.ToUtc.Value) &&
        MatchesOptional(record.ToolId, query.ToolId) &&
        MatchesOptional(record.Status, query.Status) &&
        MatchesOptional(record.ClientId, query.ClientId) &&
        MatchesOptional(record.CorrelationId, query.CorrelationId);

    private static bool MatchesOptional(string? actual, string? expected) =>
        string.IsNullOrWhiteSpace(expected) ||
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private async Task<JsonElement?> ReadSnapshotAsync(
        string tenantId,
        string? reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var tenantDirectory = Path.GetFullPath(paths.GetTenantDirectory(tenantId));
        var candidate = Path.GetFullPath(Path.Combine(
            paths.RootPath,
            reference.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(tenantDirectory, candidate);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            logger.LogWarning("Skipped audit snapshot reference outside the tenant directory.");
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(candidate);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not read audit snapshot {SnapshotReference}.", reference);
            return null;
        }
    }
}