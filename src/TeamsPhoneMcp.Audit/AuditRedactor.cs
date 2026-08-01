using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamsPhoneMcp.Audit;

/// <summary>
/// Removes secret material from anything bound for the audit trail (build spec
/// §9.1, §7). Two independent layers run so a single mistake cannot leak:
/// <list type="number">
///   <item>name-based — manifest <c>redactParams</c> plus a built-in list of
///   parameter names that are secret by convention;</item>
///   <item>value-based — patterns that are secret regardless of where they
///   appear (PEM blocks, certificate thumbprints, JWTs).</item>
/// </list>
/// </summary>
public static class AuditRedactor
{
    public const string RedactedPlaceholder = "***redacted***";

    /// <summary>
    /// Names that always carry secret material. Matched case-insensitively as
    /// substrings so <c>clientSecret</c> and <c>appSecret</c> are both caught.
    /// <c>credentialRef</c> is deliberately absent: it is a name, never material.
    /// </summary>
    private static readonly string[] SensitiveNameFragments =
    [
        "secret",
        "password",
        "passphrase",
        "privatekey",
        "thumbprint",
        "apikey",
        "accesstoken",
        "refreshtoken",
        "bearertoken",
        "certificatedata",
        "pfx",
    ];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex PemBlockPattern = new(
        @"-----BEGIN[A-Z ]*(PRIVATE KEY|CERTIFICATE)-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex ThumbprintPattern = new(
        @"\b[0-9a-fA-F]{40}\b",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex JwtPattern = new(
        @"\bey[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// Returns a redacted copy of <paramref name="parameters"/>. Objects and
    /// arrays are walked recursively so nested payloads are covered too.
    /// </summary>
    public static JsonElement Redact(JsonElement parameters, IReadOnlyCollection<string>? redactParams)
    {
        var declared = new HashSet<string>(redactParams ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRedacted(writer, parameters, declared, propertyName: null);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Scrubs free text (log lines, exception messages) of value-shaped secrets.
    /// </summary>
    public static string? ScrubText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (PemBlockPattern.IsMatch(value))
        {
            return RedactedPlaceholder;
        }

        var scrubbed = ThumbprintPattern.Replace(value, RedactedPlaceholder);
        scrubbed = JwtPattern.Replace(scrubbed, RedactedPlaceholder);
        return scrubbed;
    }

    /// <summary>True when a parameter name is secret by manifest declaration or convention.</summary>
    public static bool IsSensitiveName(string name, ISet<string> declared)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(declared);

        if (declared.Contains(name))
        {
            return true;
        }

        foreach (var fragment in SensitiveNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteRedacted(
        Utf8JsonWriter writer,
        JsonElement element,
        HashSet<string> declared,
        string? propertyName)
    {
        var nameIsSensitive = propertyName is not null && IsSensitiveName(propertyName, declared);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (nameIsSensitive)
                {
                    writer.WriteStringValue(RedactedPlaceholder);
                    return;
                }

                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedacted(writer, property.Value, declared, property.Name);
                }

                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                if (nameIsSensitive)
                {
                    writer.WriteStringValue(RedactedPlaceholder);
                    return;
                }

                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    // Array items inherit the sensitivity of their property name.
                    WriteRedacted(writer, item, declared, propertyName);
                }

                writer.WriteEndArray();
                return;

            case JsonValueKind.String:
                if (nameIsSensitive)
                {
                    writer.WriteStringValue(RedactedPlaceholder);
                    return;
                }

                writer.WriteStringValue(ScrubText(element.GetString()));
                return;

            default:
                if (nameIsSensitive)
                {
                    writer.WriteStringValue(RedactedPlaceholder);
                    return;
                }

                element.WriteTo(writer);
                return;
        }
    }
}
