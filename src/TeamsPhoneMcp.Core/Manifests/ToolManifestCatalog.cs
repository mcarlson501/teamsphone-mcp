using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace TeamsPhoneMcp.Core.Manifests;

public interface IToolManifestCatalog
{
    IReadOnlyList<ToolManifest> All { get; }

    ToolManifest GetRequired(string toolId);
}

public sealed class ToolManifestCatalog : IToolManifestCatalog
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(ToolManifestSchema.Json);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();
    private static readonly ISerializer JsonCompatibleYamlSerializer = new SerializerBuilder().JsonCompatible().Build();

    private readonly IReadOnlyDictionary<string, ToolManifest> _manifestsById;

    public ToolManifestCatalog(string toolsRootPath, ILogger<ToolManifestCatalog> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRootPath);
        ArgumentNullException.ThrowIfNull(logger);

        var manifests = LoadManifests(toolsRootPath);
        logger.LogInformation("Loaded {ManifestCount} tool manifests from {ToolsRootPath}.", manifests.Count, toolsRootPath);
        _manifestsById = manifests.ToDictionary(m => m.Id, StringComparer.Ordinal);
        All = manifests;
    }

    public IReadOnlyList<ToolManifest> All { get; }

    public ToolManifest GetRequired(string toolId)
    {
        if (!_manifestsById.TryGetValue(toolId, out var manifest))
        {
            throw new InvalidOperationException($"No manifest found for tool '{toolId}'.");
        }

        return manifest;
    }

    private static IReadOnlyList<ToolManifest> LoadManifests(string toolsRootPath)
    {
        if (!Directory.Exists(toolsRootPath))
        {
            throw new InvalidOperationException($"Tools folder '{toolsRootPath}' does not exist.");
        }

        var manifests = new List<ToolManifest>();
        var manifestFiles = Directory.GetFiles(toolsRootPath, "manifest.yaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}_template{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        foreach (var manifestPath in manifestFiles)
        {
            var directoryName = Path.GetFileName(Path.GetDirectoryName(manifestPath));
            var manifest = ParseAndValidateManifest(manifestPath);

            if (!string.Equals(manifest.Id, directoryName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Manifest id '{manifest.Id}' must match its folder name '{directoryName}' ({manifestPath}).");
            }

            if (IsGenericExecutionName(manifest.Id))
            {
                throw new InvalidOperationException($"Unsafe tool id '{manifest.Id}' is blocked by registry policy.");
            }

            manifests.Add(manifest);
        }

        if (manifests.Count == 0)
        {
            throw new InvalidOperationException($"No tool manifests found under '{toolsRootPath}'.");
        }

        var duplicateIds = manifests
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateIds.Count > 0)
        {
            throw new InvalidOperationException($"Duplicate tool manifest ids found: {string.Join(", ", duplicateIds)}.");
        }

        return manifests;
    }

    private static ToolManifest ParseAndValidateManifest(string manifestPath)
    {
        var yaml = File.ReadAllText(manifestPath);
        var yamlObject = YamlDeserializer.Deserialize<object>(yaml);
        var json = JsonCompatibleYamlSerializer.Serialize(yamlObject);

        var node = JsonNode.Parse(json)
            ?? throw new InvalidOperationException($"Manifest '{manifestPath}' is empty.");

        var evaluation = Schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!evaluation.IsValid)
        {
            var details = string.Join("; ", evaluation.Details.Select(d => d.ToString()));
            throw new InvalidOperationException($"Manifest '{manifestPath}' failed schema validation: {details}");
        }

        var manifest = JsonSerializer.Deserialize<ToolManifest>(node, ManifestJsonOptions)
            ?? throw new InvalidOperationException($"Manifest '{manifestPath}' could not be parsed.");

        return manifest;
    }

    private static bool IsGenericExecutionName(string toolId)
    {
        return toolId.Contains("run", StringComparison.OrdinalIgnoreCase) ||
               toolId.Contains("exec", StringComparison.OrdinalIgnoreCase) ||
               toolId.Contains("invoke-command", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ToolManifestSchema
{
    internal const string Json = """
        {
          "type": "object",
          "required": ["id", "version", "summary", "category", "riskTier", "annotations", "inputs", "maxBlastRadius", "timeoutSeconds"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "string", "pattern": "^[a-z0-9]+(-[a-z0-9]+)*$" },
            "version": { "type": "string", "pattern": "^\\d+\\.\\d+\\.\\d+$" },
            "summary": { "type": "string", "minLength": 1 },
            "category": { "type": "string", "enum": ["move", "add", "change", "delete", "read"] },
            "riskTier": { "type": "integer", "minimum": 0, "maximum": 3 },
            "telephonyModels": {
              "type": "array",
              "items": { "type": "string" }
            },
            "annotations": {
              "type": "object",
              "required": ["readOnlyHint", "destructiveHint", "idempotentHint"],
              "additionalProperties": false,
              "properties": {
                "readOnlyHint": { "type": "boolean" },
                "destructiveHint": { "type": "boolean" },
                "idempotentHint": { "type": "boolean" }
              }
            },
            "inputs": {
              "type": "object",
              "minProperties": 1,
              "additionalProperties": {
                "type": "object",
                "required": ["type", "required"],
                "additionalProperties": false,
                "properties": {
                  "type": { "type": "string", "enum": ["string", "integer", "boolean", "number"] },
                  "format": { "type": "string" },
                  "required": { "type": "boolean" }
                }
              }
            },
            "maxBlastRadius": { "type": "integer", "minimum": 1 },
            "timeoutSeconds": { "type": "integer", "minimum": 1 }
          }
        }
        """;
}
