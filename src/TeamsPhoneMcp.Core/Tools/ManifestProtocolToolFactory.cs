using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TeamsPhoneMcp.Core.Manifests;

namespace TeamsPhoneMcp.Core.Tools;

internal static class ManifestProtocolToolFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static Tool Build(ToolManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var (name, input) in manifest.Inputs)
        {
            var schema = new JsonObject { ["type"] = MapSchemaType(input.Type) };
            if (!string.IsNullOrWhiteSpace(input.Format))
            {
                schema["format"] = input.Format;
            }

            if (input.AllowedValues is not null)
            {
                var allowedValues = new JsonArray();
                foreach (var value in input.AllowedValues)
                {
                    allowedValues.Add(value);
                }

                schema["enum"] = allowedValues;
            }

            if (input.Minimum.HasValue)
            {
                schema["minimum"] = input.Minimum.Value;
            }

            if (input.Maximum.HasValue)
            {
                schema["maximum"] = input.Maximum.Value;
            }

            if (input.Items is not null)
            {
                var itemSchema = new JsonObject { ["type"] = MapSchemaType(input.Items.Type) };
                if (!string.IsNullOrWhiteSpace(input.Items.Format))
                {
                    itemSchema["format"] = input.Items.Format;
                }

                schema["items"] = itemSchema;
            }

            if (input.MinItems.HasValue)
            {
                schema["minItems"] = input.MinItems.Value;
            }

            if (input.MaxItems.HasValue)
            {
                schema["maxItems"] = input.MaxItems.Value;
            }

            properties[name] = schema;
            if (input.Required)
            {
                required.Add(name);
            }
        }

        var schemaNode = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
        {
            schemaNode["required"] = required;
        }

        return new Tool
        {
            Name = manifest.Id,
            Description = manifest.Summary,
            InputSchema = JsonSerializer.SerializeToElement(schemaNode, SerializerOptions),
            Annotations = new ToolAnnotations
            {
                Title = manifest.Id,
                ReadOnlyHint = manifest.Annotations.ReadOnlyHint,
                DestructiveHint = manifest.Annotations.DestructiveHint,
                IdempotentHint = manifest.Annotations.IdempotentHint,
            },
        };
    }

    private static string MapSchemaType(string manifestType) => manifestType switch
    {
        "string" => "string",
        "integer" => "integer",
        "number" => "number",
        "boolean" => "boolean",
        "array" => "array",
        _ => "string",
    };
}