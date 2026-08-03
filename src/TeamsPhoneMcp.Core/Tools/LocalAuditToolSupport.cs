using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace TeamsPhoneMcp.Core.Tools;

internal static class LocalAuditToolSupport
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string? OptionalString(
        IReadOnlyDictionary<string, JsonElement> arguments,
        string name) =>
        arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool TryParseUtc(string? value, out DateTimeOffset? parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = null;
            return true;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            parsed = timestamp.ToUniversalTime();
            return true;
        }

        parsed = null;
        return false;
    }

    public static CallToolResult ToCallToolResult<T>(T result, string summary, bool isError) =>
        new()
        {
            StructuredContent = JsonSerializer.SerializeToElement(result, SerializerOptions),
            IsError = isError,
            Content = [new TextContentBlock { Text = summary }],
        };
}