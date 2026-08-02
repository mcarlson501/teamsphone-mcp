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

            if (string.Equals(input.Type, "array", StringComparison.Ordinal))
            {
                ValidateArray(inputName, input, value, validationErrors);
                continue;
            }

            if (string.Equals(input.Format, "upn", StringComparison.Ordinal) &&
                !UpnRegex.IsMatch(value.GetString()!))
            {
                validationErrors.Add($"argument '{inputName}' must be a valid UPN");
            }

            if (input.AllowedValues is not null &&
                !input.AllowedValues.Contains(value.GetString()!, StringComparer.Ordinal))
            {
                validationErrors.Add(
                    $"argument '{inputName}' must be one of: {string.Join(", ", input.AllowedValues)}");
            }

            ValidateNumericBounds(inputName, input, value, validationErrors);
        }

        if (validationErrors.Count > 0)
        {
            throw new McpException(
                $"Tool '{manifest.Id}' arguments are invalid: {string.Join("; ", validationErrors)}.");
        }
    }

    private static void ValidateArray(
        string inputName,
        ToolManifestInput input,
        JsonElement value,
        ICollection<string> validationErrors)
    {
        var itemCount = value.GetArrayLength();
        if (input.MinItems.HasValue && itemCount < input.MinItems.Value)
        {
            validationErrors.Add($"argument '{inputName}' must contain at least {input.MinItems.Value} items");
        }
        if (input.MaxItems.HasValue && itemCount > input.MaxItems.Value)
        {
            validationErrors.Add($"argument '{inputName}' must contain at most {input.MaxItems.Value} items");
        }

        if (input.Items is null)
        {
            return;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (!HasExpectedType(item, input.Items.Type))
            {
                validationErrors.Add(
                    $"argument '{inputName}[{index}]' must be {GetExpectedTypeDescription(input.Items.Type)}");
            }
            else if (string.Equals(input.Items.Format, "upn", StringComparison.Ordinal) &&
                     !UpnRegex.IsMatch(item.GetString()!))
            {
                validationErrors.Add($"argument '{inputName}[{index}]' must be a valid UPN");
            }

            index++;
        }
    }

    private static void ValidateNumericBounds(
        string inputName,
        ToolManifestInput input,
        JsonElement value,
        ICollection<string> validationErrors)
    {
        if ((input.Minimum is null && input.Maximum is null) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out var numericValue))
        {
            return;
        }

        if (input.Minimum.HasValue && input.Maximum.HasValue &&
            (numericValue < input.Minimum.Value || numericValue > input.Maximum.Value))
        {
            validationErrors.Add(
                $"argument '{inputName}' must be between {input.Minimum.Value} and {input.Maximum.Value}");
        }
        else if (input.Minimum.HasValue && numericValue < input.Minimum.Value)
        {
            validationErrors.Add($"argument '{inputName}' must be at least {input.Minimum.Value}");
        }
        else if (input.Maximum.HasValue && numericValue > input.Maximum.Value)
        {
            validationErrors.Add($"argument '{inputName}' must be at most {input.Maximum.Value}");
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
            "array" => value.ValueKind == JsonValueKind.Array,
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
            "array" => "an array",
            _ => $"of type '{expectedType}'"
        };
}