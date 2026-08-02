using System.Text.Json.Serialization;

namespace TeamsPhoneMcp.Core.Manifests;

public sealed record ToolManifest
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string Summary { get; init; }

    public required string Category { get; init; }

    public required int RiskTier { get; init; }

    public List<string> TelephonyModels { get; init; } = [];

    public required ToolManifestAnnotations Annotations { get; init; }

    public required Dictionary<string, ToolManifestInput> Inputs { get; init; }

    public List<string> RedactParams { get; init; } = [];

    public int MaxBlastRadius { get; init; }

    public int TimeoutSeconds { get; init; }

    /// <summary>Human-readable preflight checks the tool's <c>preflight</c> stage implements (build spec §6.1).</summary>
    public List<string> Preflight { get; init; } = [];

    /// <summary>Human-readable verification checks the tool's <c>verify</c> stage implements.</summary>
    public List<string> Verification { get; init; } = [];

    /// <summary>Human-readable description of what the tool's <c>rollback</c> stage undoes.</summary>
    public string? Rollback { get; init; }
}

/// <summary>Declarative input schema enforced before MCP tool argument binding.</summary>
public sealed record ToolManifestInput
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("allowedValues")]
    public List<string>? AllowedValues { get; init; }

    [JsonPropertyName("minimum")]
    public decimal? Minimum { get; init; }

    [JsonPropertyName("maximum")]
    public decimal? Maximum { get; init; }

    [JsonPropertyName("items")]
    public ToolManifestArrayItems? Items { get; init; }

    [JsonPropertyName("minItems")]
    public int? MinItems { get; init; }

    [JsonPropertyName("maxItems")]
    public int? MaxItems { get; init; }
}

public sealed record ToolManifestArrayItems
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }
}

public sealed record ToolManifestAnnotations
{
    public bool ReadOnlyHint { get; init; }

    public bool DestructiveHint { get; init; }

    public bool IdempotentHint { get; init; }
}
