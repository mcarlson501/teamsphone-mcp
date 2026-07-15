using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamsPhoneMcp.Core.Policy;

namespace TeamsPhoneMcp.UnitTests;

public class ConfirmationTokenServiceTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonpositiveTtl(int ttlSeconds)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConfirmationTokenService(Key, TimeSpan.FromSeconds(ttlSeconds)));

        Assert.Equal("ttl", exception.ParamName);
    }

    [Fact]
    public void Validate_AcceptsCanonicalEquivalentParameters()
    {
        var service = CreateService();
        var issuedParameters = ParseJson("""{"policyName":"Global","targetUserUpn":"user@example.com"}""");
        var reorderedParameters = ParseJson("""{"targetUserUpn":"user@example.com","policyName":"Global"}""");
        var token = service.Issue("test-tool", "tenant-a", issuedParameters, Now);

        var validation = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            reorderedParameters,
            Now.AddMinutes(1));

        Assert.True(validation.IsValid);
        Assert.Null(validation.ErrorCode);
    }

    [Fact]
    public void Validate_TreatsExactExpiryBoundaryAsExpired()
    {
        var service = CreateService();
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var token = service.Issue("test-tool", "tenant-a", parameters, Now);

        var validation = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            parameters,
            Now.AddMinutes(15));

        Assert.False(validation.IsValid);
        Assert.Equal("expiredConfirmationToken", validation.ErrorCode);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("invalid.token.extra")]
    [InlineData("%%%.%%%")] 
    public void Validate_RejectsMalformedToken(string token)
    {
        var service = CreateService();

        var validation = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            ParseJson("{}"),
            Now);

        Assert.False(validation.IsValid);
        Assert.Equal("invalidConfirmationToken", validation.ErrorCode);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Validate_RejectsSignedMalformedPayloadWithoutThrowing(string payloadJson)
    {
        var service = CreateService();
        var token = CreateSignedToken(payloadJson);

        var validation = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            ParseJson("{}"),
            Now);

        Assert.False(validation.IsValid);
        Assert.Equal("invalidConfirmationToken", validation.ErrorCode);
    }

    [Theory]
    [InlineData("other-tool", "tenant-a", "{\"targetUserUpn\":\"user@example.com\"}")]
    [InlineData("test-tool", "tenant-b", "{\"targetUserUpn\":\"user@example.com\"}")]
    [InlineData("test-tool", "tenant-a", "{\"targetUserUpn\":\"other@example.com\"}")]
    public void Validate_RejectsCrossContextTokenUse(
        string toolId,
        string tenantId,
        string parametersJson)
    {
        var service = CreateService();
        var issuedParameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var token = service.Issue("test-tool", "tenant-a", issuedParameters, Now);

        var validation = service.Validate(
            token,
            toolId,
            tenantId,
            ParseJson(parametersJson),
            Now.AddMinutes(1));

        Assert.False(validation.IsValid);
        Assert.Equal("invalidConfirmationToken", validation.ErrorCode);
    }

    [Fact]
    public void Validate_RejectsTamperedSignature()
    {
        var service = CreateService();
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var token = service.Issue("test-tool", "tenant-a", parameters, Now);
        var tamperedToken = $"{token[..^1]}{(token[^1] == 'A' ? 'B' : 'A')}";

        var validation = service.Validate(
            tamperedToken,
            "test-tool",
            "tenant-a",
            parameters,
            Now.AddMinutes(1));

        Assert.False(validation.IsValid);
        Assert.Equal("invalidConfirmationToken", validation.ErrorCode);
    }

    private static ConfirmationTokenService CreateService() =>
        new(Key, TimeSpan.FromMinutes(15));

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string CreateSignedToken(string payloadJson)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        using var hmac = new HMACSHA256(Key);
        var signature = hmac.ComputeHash(payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}