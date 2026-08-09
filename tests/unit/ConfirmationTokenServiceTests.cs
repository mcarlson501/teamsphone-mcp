using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamsPhoneMcp.Core.Policy;

namespace TeamsPhoneMcp.UnitTests;

public class ConfirmationTokenServiceTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly ConfirmationTokenBinding Caller = new("session-a", "orchestrator");

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
        var token = service.Issue("test-tool", "tenant-a", issuedParameters, Caller, Now);

        var validation = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            reorderedParameters,
            Caller,
            Now.AddMinutes(1));

        Assert.True(validation.IsValid);
        Assert.Null(validation.ErrorCode);
    }

    [Fact]
    public void Validate_TreatsExactExpiryBoundaryAsExpired()
    {
        var service = CreateService();
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var token = service.Issue("test-tool", "tenant-a", parameters, Caller, Now);

        var validation = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            parameters,
            Caller,
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
            Caller,
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
            Caller,
            Now);

        Assert.False(validation.IsValid);
        Assert.Equal("invalidConfirmationToken", validation.ErrorCode);
    }

    /// <summary>A token in the pre-S2 payload shape carries no jti and must not validate.</summary>
    [Fact]
    public void Validate_RejectsSignedPayloadWithoutAJti()
    {
        var service = CreateService();
        var expiresAt = Now.AddMinutes(15).ToUnixTimeSeconds();
        var token = CreateSignedToken(
            $$"""{"ToolId":"test-tool","TenantId":"tenant-a","ParamsHash":"00","ExpiresAtUnixSeconds":{{expiresAt}}}""");

        var validation = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            ParseJson("{}"),
            Caller,
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
        var token = service.Issue("test-tool", "tenant-a", issuedParameters, Caller, Now);

        var validation = service.Validate(
            token,
            toolId,
            tenantId,
            ParseJson(parametersJson),
            Caller,
            Now.AddMinutes(1));

        Assert.False(validation.IsValid);
        Assert.Equal("invalidConfirmationToken", validation.ErrorCode);
    }

    [Fact]
    public void Validate_RejectsTamperedSignature()
    {
        var service = CreateService();
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var token = service.Issue("test-tool", "tenant-a", parameters, Caller, Now);
        var tamperedToken = $"{token[..^1]}{(token[^1] == 'A' ? 'B' : 'A')}";

        var validation = service.Validate(
            tamperedToken,
            "test-tool",
            "tenant-a",
            parameters,
            Caller,
            Now.AddMinutes(1));

        Assert.False(validation.IsValid);
        Assert.Equal("invalidConfirmationToken", validation.ErrorCode);
    }

    [Fact]
    public void Validate_RejectsAReplayedToken()
    {
        var service = CreateService();
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var token = service.Issue("test-tool", "tenant-a", parameters, Caller, Now);

        var first = service.Validate(token, "test-tool", "tenant-a", parameters, Caller, Now.AddMinutes(1));
        var replay = service.Validate(token, "test-tool", "tenant-a", parameters, Caller, Now.AddMinutes(2));

        Assert.True(first.IsValid);
        Assert.False(replay.IsValid);
        Assert.Equal("replayedConfirmationToken", replay.ErrorCode);
    }

    [Fact]
    public void Issue_MintsADistinctTokenEachTimeForIdenticalInputs()
    {
        var service = CreateService();
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");

        var first = service.Issue("test-tool", "tenant-a", parameters, Caller, Now);
        var second = service.Issue("test-tool", "tenant-a", parameters, Caller, Now);

        // Without a per-issue jti the two tokens would be byte-identical and redeeming
        // one would burn the other.
        Assert.NotEqual(first, second);
        Assert.True(service.Validate(first, "test-tool", "tenant-a", parameters, Caller, Now.AddMinutes(1)).IsValid);
        Assert.True(service.Validate(second, "test-tool", "tenant-a", parameters, Caller, Now.AddMinutes(1)).IsValid);
    }

    [Fact]
    public void Validate_RejectsATokenRedeemedInAnotherSession()
    {
        var service = CreateService();
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var token = service.Issue("test-tool", "tenant-a", parameters, Caller, Now);

        var validation = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            parameters,
            Caller with { SessionId = "session-b" },
            Now.AddMinutes(1));

        Assert.False(validation.IsValid);
        Assert.Equal("sessionBoundConfirmationToken", validation.ErrorCode);
    }

    [Fact]
    public void Validate_RejectsATokenRedeemedByAnotherClient()
    {
        var service = CreateService();
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var token = service.Issue("test-tool", "tenant-a", parameters, Caller, Now);

        var validation = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            parameters,
            Caller with { ClientId = "other-client" },
            Now.AddMinutes(1));

        Assert.False(validation.IsValid);
        Assert.Equal("clientBoundConfirmationToken", validation.ErrorCode);
    }

    /// <summary>
    /// A rejected redemption must not spend the token, or any failed attempt would lock a
    /// client out of its own confirmed write.
    /// </summary>
    [Fact]
    public void Validate_DoesNotSpendTheTokenWhenAnEarlierCheckFails()
    {
        var service = CreateService();
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var token = service.Issue("test-tool", "tenant-a", parameters, Caller, Now);

        var wrongSession = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            parameters,
            Caller with { SessionId = "session-b" },
            Now.AddMinutes(1));
        var correctSession = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            parameters,
            Caller,
            Now.AddMinutes(2));

        Assert.False(wrongSession.IsValid);
        Assert.True(correctSession.IsValid);
    }

    [Fact]
    public void Validate_FailsClosedWhenTheReplayCacheIsExhausted()
    {
        var service = new ConfirmationTokenService(
            Key,
            TimeSpan.FromMinutes(15),
            new ConsumedConfirmationTokenCache(capacity: 1));
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var first = service.Issue("test-tool", "tenant-a", parameters, Caller, Now);
        var second = service.Issue("test-tool", "tenant-a", parameters, Caller, Now);

        Assert.True(service.Validate(first, "test-tool", "tenant-a", parameters, Caller, Now.AddMinutes(1)).IsValid);

        var exhausted = service.Validate(second, "test-tool", "tenant-a", parameters, Caller, Now.AddMinutes(1));

        Assert.False(exhausted.IsValid);
        Assert.Equal("confirmationTokenCacheExhausted", exhausted.ErrorCode);
    }

    [Fact]
    public void Validate_AcceptsUnboundCallersOnTransportsWithoutSessionOrClient()
    {
        var service = CreateService();
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var token = service.Issue("test-tool", "tenant-a", parameters, ConfirmationTokenBinding.None, Now);

        var validation = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            parameters,
            ConfirmationTokenBinding.None,
            Now.AddMinutes(1));

        Assert.True(validation.IsValid);
    }

    /// <summary>A stdio caller must not be able to spend a token minted for an HTTP session.</summary>
    [Fact]
    public void Validate_RejectsAnUnboundCallerRedeemingABoundToken()
    {
        var service = CreateService();
        var parameters = ParseJson("""{"targetUserUpn":"user@example.com"}""");
        var token = service.Issue("test-tool", "tenant-a", parameters, Caller, Now);

        var validation = service.Validate(
            token,
            "test-tool",
            "tenant-a",
            parameters,
            ConfirmationTokenBinding.None,
            Now.AddMinutes(1));

        Assert.False(validation.IsValid);
        Assert.Equal("sessionBoundConfirmationToken", validation.ErrorCode);
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
