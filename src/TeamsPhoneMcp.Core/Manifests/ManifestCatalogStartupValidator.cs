using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace TeamsPhoneMcp.Core.Manifests;

public sealed class ManifestCatalogStartupValidator(
    IToolManifestCatalog catalog,
    IEnumerable<McpServerTool> registeredTools,
    ILogger<ManifestCatalogStartupValidator> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var tools = registeredTools.ToList();
        var registeredIds = tools.Select(tool => tool.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            var protocolTool = tool.ProtocolTool;
            var manifest = catalog.GetRequired(protocolTool.Name);
            ValidateAnnotations(manifest, protocolTool.Annotations?.ReadOnlyHint, protocolTool.Annotations?.DestructiveHint, protocolTool.Annotations?.IdempotentHint);
            ValidateInputSchema(manifest, protocolTool.InputSchema);
        }

        var unregisteredManifest = catalog.All.FirstOrDefault(manifest => !registeredIds.Contains(manifest.Id));
        if (unregisteredManifest is not null)
        {
            throw new InvalidOperationException(
                $"Manifest '{unregisteredManifest.Id}' does not have a registered MCP tool handler.");
        }

        logger.LogInformation(
            "Validated {ManifestCount} tool manifests against {ToolCount} registered MCP tools at startup.",
            catalog.All.Count,
            tools.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidateAnnotations(
        ToolManifest manifest,
        bool? readOnlyHint,
        bool? destructiveHint,
        bool? idempotentHint)
    {
        if (manifest.Annotations.ReadOnlyHint != readOnlyHint ||
            manifest.Annotations.DestructiveHint != destructiveHint ||
            manifest.Annotations.IdempotentHint != idempotentHint)
        {
            throw new InvalidOperationException(
                $"Manifest '{manifest.Id}' annotations do not match its registered MCP tool annotations.");
        }
    }

    private static void ValidateInputSchema(ToolManifest manifest, System.Text.Json.JsonElement inputSchema)
    {
        if (!inputSchema.TryGetProperty("properties", out var properties) || properties.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Registered MCP tool '{manifest.Id}' has an invalid input schema.");
        }

        var requiredInputs = inputSchema.TryGetProperty("required", out var required) &&
                             required.ValueKind == System.Text.Json.JsonValueKind.Array
            ? required.EnumerateArray().Select(item => item.GetString()).Where(name => name is not null).ToHashSet(StringComparer.Ordinal)
            : [];
        var registeredInputNames = properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        if (!registeredInputNames.SetEquals(manifest.Inputs.Keys))
        {
            throw new InvalidOperationException(
                $"Manifest '{manifest.Id}' inputs do not match its registered MCP tool input schema.");
        }

        foreach (var (inputName, input) in manifest.Inputs)
        {
            var propertySchema = properties.GetProperty(inputName);
            if (!SchemaContainsType(propertySchema, input.Type) ||
                requiredInputs.Contains(inputName) != input.Required ||
                !SchemaContainsConstraints(propertySchema, input))
            {
                throw new InvalidOperationException(
                    $"Manifest '{manifest.Id}' input '{inputName}' does not match its registered MCP tool input schema.");
            }
        }
    }

    private static bool SchemaContainsConstraints(
        System.Text.Json.JsonElement schema,
        ToolManifestInput input) =>
        SchemaContainsAllowedValues(schema, input.AllowedValues) &&
        SchemaContainsDecimal(schema, "minimum", input.Minimum) &&
        SchemaContainsDecimal(schema, "maximum", input.Maximum);

    private static bool SchemaContainsAllowedValues(
        System.Text.Json.JsonElement schema,
        IReadOnlyCollection<string>? allowedValues)
    {
        if (!schema.TryGetProperty("enum", out var schemaValues))
        {
            return allowedValues is null;
        }

        if (allowedValues is null || schemaValues.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return false;
        }

        var values = schemaValues.EnumerateArray()
            .Where(value => value.ValueKind == System.Text.Json.JsonValueKind.String)
            .Select(value => value.GetString()!)
            .ToList();
        return values.Count == allowedValues.Count &&
               values.ToHashSet(StringComparer.Ordinal).SetEquals(allowedValues);
    }

    private static bool SchemaContainsDecimal(
        System.Text.Json.JsonElement schema,
        string propertyName,
        decimal? expected)
    {
        if (!schema.TryGetProperty(propertyName, out var value))
        {
            return expected is null;
        }

        return expected.HasValue && value.TryGetDecimal(out var actual) && actual == expected.Value;
    }

    private static bool SchemaContainsType(System.Text.Json.JsonElement schema, string manifestType)
    {
        if (!schema.TryGetProperty("type", out var type))
        {
            return false;
        }

        return type.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => string.Equals(type.GetString(), manifestType, StringComparison.Ordinal),
            System.Text.Json.JsonValueKind.Array => type.EnumerateArray()
                .Any(item => string.Equals(item.GetString(), manifestType, StringComparison.Ordinal)),
            _ => false
        };
    }
}