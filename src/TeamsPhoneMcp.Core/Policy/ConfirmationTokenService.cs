using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeamsPhoneMcp.Core.Policy;

public interface IConfirmationTokenService
{
    string Issue(string toolId, string tenantId, JsonElement toolParams, DateTimeOffset nowUtc);

    ConfirmationTokenValidation Validate(
        string token,
        string toolId,
        string tenantId,
        JsonElement toolParams,
        DateTimeOffset nowUtc);
}

public sealed class ConfirmationTokenService : IConfirmationTokenService
{
    private readonly byte[] _key;
    private readonly TimeSpan _ttl;

    public ConfirmationTokenService(byte[] key, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
        {
            throw new ArgumentException("Confirmation token key must be at least 32 bytes.", nameof(key));
        }

        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Confirmation token TTL must be positive.");
        }

        _key = key.ToArray();
        _ttl = ttl;
    }

    public static ConfirmationTokenService FromBase64Key(string base64Key, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        return new ConfirmationTokenService(Convert.FromBase64String(base64Key), ttl);
    }

    public static string CreateRandomBase64Key()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public string Issue(string toolId, string tenantId, JsonElement toolParams, DateTimeOffset nowUtc)
    {
        var payload = new ConfirmationTokenPayload
        {
            ToolId = toolId,
            TenantId = tenantId,
            ParamsHash = ComputeParamsHash(toolParams),
            ExpiresAtUnixSeconds = nowUtc.Add(_ttl).ToUnixTimeSeconds()
        };

        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var signature = ComputeHmac(payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    public ConfirmationTokenValidation Validate(
        string token,
        string toolId,
        string tenantId,
        JsonElement toolParams,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ConfirmationTokenValidation.Fail("missingConfirmationToken");
        }

        var parts = token.Split('.', 2);
        if (parts.Length != 2)
        {
            return ConfirmationTokenValidation.Fail("invalidConfirmationToken");
        }

        byte[] payloadBytes;
        byte[] providedSignature;
        try
        {
            payloadBytes = Base64UrlDecode(parts[0]);
            providedSignature = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            return ConfirmationTokenValidation.Fail("invalidConfirmationToken");
        }

        var expectedSignature = ComputeHmac(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
        {
            return ConfirmationTokenValidation.Fail("invalidConfirmationToken");
        }

        ConfirmationTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ConfirmationTokenPayload>(payloadBytes);
        }
        catch (JsonException)
        {
            return ConfirmationTokenValidation.Fail("invalidConfirmationToken");
        }

        if (payload is null)
        {
            return ConfirmationTokenValidation.Fail("invalidConfirmationToken");
        }

        if (!string.Equals(payload.ToolId, toolId, StringComparison.Ordinal) ||
            !string.Equals(payload.TenantId, tenantId, StringComparison.Ordinal))
        {
            return ConfirmationTokenValidation.Fail("invalidConfirmationToken");
        }

        if (payload.ExpiresAtUnixSeconds <= nowUtc.ToUnixTimeSeconds())
        {
            return ConfirmationTokenValidation.Fail("expiredConfirmationToken");
        }

        var paramsHash = ComputeParamsHash(toolParams);
        if (!string.Equals(payload.ParamsHash, paramsHash, StringComparison.Ordinal))
        {
            return ConfirmationTokenValidation.Fail("invalidConfirmationToken");
        }

        return ConfirmationTokenValidation.Success();
    }

    private static string ComputeParamsHash(JsonElement toolParams)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(toolParams, writer);
        }

        var hash = SHA256.HashData(stream.ToArray());
        return Convert.ToHexString(hash);
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private byte[] ComputeHmac(byte[] payloadBytes)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(payloadBytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record ConfirmationTokenPayload
    {
        public required string ToolId { get; init; }

        public required string TenantId { get; init; }

        public required string ParamsHash { get; init; }

        public required long ExpiresAtUnixSeconds { get; init; }
    }
}

public readonly record struct ConfirmationTokenValidation(bool IsValid, string? ErrorCode)
{
    public static ConfirmationTokenValidation Success() => new(true, null);

    public static ConfirmationTokenValidation Fail(string code) => new(false, code);
}
