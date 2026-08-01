using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamsPhoneMcp.Audit;

/// <summary>
/// One immutable audit record per tool call — reads, failures, dry-runs and
/// policy rejections included (build spec §9.1). Serialized as a single JSONL
/// line so the trail stays greppable and <c>jq</c>-able without the server.
/// </summary>
public sealed record AuditRecord
{
    /// <summary>Schema version so downstream readers can evolve safely.</summary>
    public int RecordVersion { get; init; } = 1;

    public required DateTimeOffset Timestamp { get; init; }

    public required string CorrelationId { get; init; }

    public string? SessionId { get; init; }

    public string? ClientId { get; init; }

    /// <summary>Tenant the call targeted; also selects the JSONL file it lands in.</summary>
    public required string TenantId { get; init; }

    public required string ToolId { get; init; }

    public required string ToolVersion { get; init; }

    public required string Status { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>
    /// Raw failure detail. Clients only ever see the sanitized envelope message;
    /// the unabridged text is retained here for forensics (build spec §7).
    /// </summary>
    public string? ErrorMessage { get; init; }

    public bool DryRun { get; init; }

    /// <summary>True when the call ran under whatIf / simulated server mode.</summary>
    public bool Simulated { get; init; }

    public int RiskTier { get; init; }

    /// <summary>Business parameters after manifest-driven and pattern-based redaction.</summary>
    [JsonConverter(typeof(NullableJsonElementConverter))]
    public JsonElement? Parameters { get; init; }

    public IReadOnlyList<AuditStageTiming> Stages { get; init; } = Array.Empty<AuditStageTiming>();

    public IReadOnlyList<AuditCheckResult> Checks { get; init; } = Array.Empty<AuditCheckResult>();

    public long DurationMs { get; init; }

    /// <summary>References to the separately stored before/after state artifacts.</summary>
    public AuditSnapshotRefs? SnapshotRefs { get; init; }
}

/// <summary>Wall-clock cost of a single pipeline stage.</summary>
public sealed record AuditStageTiming(string Stage, long DurationMs);

/// <summary>A preflight or verification check outcome carried into the trail.</summary>
public sealed record AuditCheckResult(string Phase, string Check, bool Passed, string? Detail);

/// <summary>Relative paths (under the tenant's audit folder) of stored state snapshots.</summary>
public sealed record AuditSnapshotRefs(string? Before, string? After);

/// <summary>
/// <see cref="JsonElement"/> has no built-in converter for the nullable form,
/// so null snapshots serialize as JSON null instead of throwing.
/// </summary>
internal sealed class NullableJsonElementConverter : JsonConverter<JsonElement?>
{
    public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }

    public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        value.Value.WriteTo(writer);
    }
}
