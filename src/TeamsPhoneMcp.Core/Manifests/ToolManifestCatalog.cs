using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TeamsPhoneMcp.Core.Manifests;

public interface IToolManifestCatalog
{
    IReadOnlyList<ToolManifest> All { get; }

    ToolManifest GetRequired(string toolId);
}

public sealed class ToolManifestCatalog : IToolManifestCatalog
{
    private static readonly Regex ToolIdRegex = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex SemVerRegex = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

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
            var manifest = ParseManifest(manifestPath);
            ValidateManifest(manifest, manifestPath);

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

    private static ToolManifest ParseManifest(string manifestPath)
    {
        var yaml = File.ReadAllText(manifestPath);
        var manifest = YamlDeserializer.Deserialize<ToolManifest>(yaml);
        return manifest ?? throw new InvalidOperationException($"Manifest '{manifestPath}' could not be parsed.");
    }

    private static void ValidateManifest(ToolManifest manifest, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || !ToolIdRegex.IsMatch(manifest.Id))
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' has invalid id.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version) || !SemVerRegex.IsMatch(manifest.Version))
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' has invalid version.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Summary))
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' has invalid summary.");
        }

        if (!new[] { "move", "add", "change", "delete", "read" }.Contains(manifest.Category, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' has invalid category.");
        }

        if (manifest.RiskTier is < 0 or > 3)
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' has invalid riskTier.");
        }

        if (manifest.MaxBlastRadius < 1)
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' has invalid maxBlastRadius.");
        }

        if (manifest.TimeoutSeconds < 1)
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' has invalid timeoutSeconds.");
        }

        if (manifest.Inputs is null || manifest.Inputs.Count == 0)
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' must define at least one input.");
        }

        foreach (var (inputName, input) in manifest.Inputs)
        {
            if (string.IsNullOrWhiteSpace(inputName))
            {
                throw new InvalidOperationException($"Manifest '{manifestPath}' has an input with an empty name.");
            }

            if (input is null || !new[] { "string", "integer", "boolean", "number" }.Contains(input.Type, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Manifest '{manifestPath}' input '{inputName}' has invalid type.");
            }
        }
    }

    private static bool IsGenericExecutionName(string toolId)
    {
        return toolId.Contains("run", StringComparison.OrdinalIgnoreCase) ||
               toolId.Contains("exec", StringComparison.OrdinalIgnoreCase) ||
               toolId.Contains("invoke-command", StringComparison.OrdinalIgnoreCase);
    }
}
