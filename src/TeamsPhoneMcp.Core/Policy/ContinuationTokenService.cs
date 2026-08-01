using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TeamsPhoneMcp.Core.Policy;

public interface IContinuationTokenService
{
    string Issue(
        string toolId,
        string tenantId,
        JsonElement filters,
        int nextOffset,
        DateTimeOffset nowUtc);

    ContinuationTokenValidation Validate(
        string token,
        string toolId,
        string tenantId,
        JsonElement filters,
        DateTimeOffset nowUtc);
}

public sealed class ContinuationTokenService : IContinuationTokenService
{
    private const int PayloadVersion = 1;
    private const int MaxTokenLength = 4096;
    private static readonly byte[] KeyDerivationContext =
        "teamsphone-mcp:continuation-token:v1"u8.ToArray();

    private readonly byte[] _key;
    private readonly TimeSpan _ttl;

    public ContinuationTokenService(byte[] key, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
        {
            throw new ArgumentException("Continuation token key must be at least 32 bytes.", nameof(key));
        }

        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Continuation token TTL must be positive.");
        }

        using var hmac = new HMACSHA256(key);
        _key = hmac.ComputeHash(KeyDerivationContext);
        _ttl = ttl;
    }

    public string Issue(
        string toolId,
        string tenantId,
        JsonElement filters,
        int nextOffset,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (nextOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextOffset), "Continuation offset cannot be negative.");
        }

        var payload = new ContinuationTokenPayload
        {
            Version = PayloadVersion,
            ToolId = toolId,
            TenantId = tenantId,
            FiltersHash = ComputeCanonicalHash(filters),
            NextOffset = nextOffset,
            ExpiresAtUnixSeconds = nowUtc.Add(_ttl).ToUnixTimeSeconds()
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = ComputeHmac(payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    public ContinuationTokenValidation Validate(
        string token,
        string toolId,
        string tenantId,
        JsonElement filters,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            return ContinuationTokenValidation.Fail("invalidContinuationToken");
        }

        var parts = token.Split('.', 2);
        if (parts.Length != 2)
        {
            return ContinuationTokenValidation.Fail("invalidContinuationToken");
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
            return ContinuationTokenValidation.Fail("invalidContinuationToken");
        }

        var expectedSignature = ComputeHmac(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
        {
            return ContinuationTokenValidation.Fail("invalidContinuationToken");
        }

        ContinuationTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ContinuationTokenPayload>(payloadBytes);
        }
        catch (JsonException)
        {
            return ContinuationTokenValidation.Fail("invalidContinuationToken");
        }

        if (payload is null ||
            payload.Version != PayloadVersion ||
            payload.NextOffset < 0 ||
            !string.Equals(payload.ToolId, toolId, StringComparison.Ordinal) ||
            !string.Equals(payload.TenantId, tenantId, StringComparison.Ordinal) ||
            !string.Equals(payload.FiltersHash, ComputeCanonicalHash(filters), StringComparison.Ordinal))
        {
            return ContinuationTokenValidation.Fail("invalidContinuationToken");
        }

        if (payload.ExpiresAtUnixSeconds <= nowUtc.ToUnixTimeSeconds())
        {
            return ContinuationTokenValidation.Fail("expiredContinuationToken");
        }

        return ContinuationTokenValidation.Success(payload.NextOffset);
    }

    private static string ComputeCanonicalHash(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(value, writer);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
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

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record ContinuationTokenPayload
    {
        public required int Version { get; init; }

        public required string ToolId { get; init; }

        public required string TenantId { get; init; }

        public required string FiltersHash { get; init; }

        public required int NextOffset { get; init; }

        public required long ExpiresAtUnixSeconds { get; init; }
    }
}

public readonly record struct ContinuationTokenValidation(bool IsValid, int? NextOffset, string? ErrorCode)
{
    public static ContinuationTokenValidation Success(int nextOffset) => new(true, nextOffset, null);

    public static ContinuationTokenValidation Fail(string code) => new(false, null, code);
}