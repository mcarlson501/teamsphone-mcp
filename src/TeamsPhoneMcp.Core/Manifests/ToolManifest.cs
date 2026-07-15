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

    public int MaxBlastRadius { get; init; }

    public int TimeoutSeconds { get; init; }
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
}

public sealed record ToolManifestAnnotations
{
    public bool ReadOnlyHint { get; init; }

    public bool DestructiveHint { get; init; }

    public bool IdempotentHint { get; init; }
}
