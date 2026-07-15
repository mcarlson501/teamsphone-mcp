using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace TeamsPhoneMcp.Core.Manifests;

public static class ToolArgumentValidator
{
    private static readonly Regex UpnRegex = new("^[^@\\s]+@[^@\\s]+$", RegexOptions.Compiled);

    public static void Validate(
        ToolManifest manifest,
        IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var validationErrors = new List<string>();
        var suppliedArguments = arguments?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal) ?? new Dictionary<string, JsonElement>();

        foreach (var inputName in suppliedArguments.Keys
                     .Where(inputName => !manifest.Inputs.ContainsKey(inputName))
                     .OrderBy(inputName => inputName, StringComparer.Ordinal))
        {
            validationErrors.Add($"unknown argument '{inputName}'");
        }

        foreach (var (inputName, input) in manifest.Inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!suppliedArguments.TryGetValue(inputName, out var value))
            {
                if (input.Required)
                {
                    validationErrors.Add($"missing required argument '{inputName}'");
                }

                continue;
            }

            if (!HasExpectedType(value, input.Type))
            {
                validationErrors.Add($"argument '{inputName}' must be {GetExpectedTypeDescription(input.Type)}");
                continue;
            }

            if (input.Required &&
                string.Equals(input.Type, "string", StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                validationErrors.Add($"argument '{inputName}' must not be empty");
                continue;
            }

            if (string.Equals(input.Format, "upn", StringComparison.Ordinal) &&
                !UpnRegex.IsMatch(value.GetString()!))
            {
                validationErrors.Add($"argument '{inputName}' must be a valid UPN");
            }
        }

        if (validationErrors.Count > 0)
        {
            throw new McpException(
                $"Tool '{manifest.Id}' arguments are invalid: {string.Join("; ", validationErrors)}.");
        }
    }

    private static bool HasExpectedType(JsonElement value, string expectedType)
    {
        return expectedType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
    }

    private static string GetExpectedTypeDescription(string expectedType) =>
        expectedType switch
        {
            "string" => "a string",
            "integer" => "an integer",
            "number" => "a number",
            "boolean" => "a boolean",
            _ => $"of type '{expectedType}'"
        };
}