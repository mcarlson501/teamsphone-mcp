using System.Text.Json;
using TeamsPhoneMcp.Core.Policy;

namespace TeamsPhoneMcp.UnitTests;

public class ContinuationTokenServiceTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonpositiveTtl(int ttlSeconds)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContinuationTokenService(Key, TimeSpan.FromSeconds(ttlSeconds)));

        Assert.Equal("ttl", exception.ParamName);
    }

    [Fact]
    public void Validate_ReturnsOffsetForCanonicalEquivalentFilters()
    {
        var service = CreateService();
        var issuedFilters = ParseJson("""{"assignmentStatus":"unassigned","numberType":"callingPlan"}""");
        var reorderedFilters = ParseJson("""{"numberType":"callingPlan","assignmentStatus":"unassigned"}""");
        var token = service.Issue("list-phone-numbers", "tenant-a", issuedFilters, 100, Now);

        var validation = service.Validate(
            token,
            "list-phone-numbers",
            "tenant-a",
            reorderedFilters,
            Now.AddMinutes(1));

        Assert.True(validation.IsValid);
        Assert.Equal(100, validation.NextOffset);
        Assert.Null(validation.ErrorCode);
    }

    [Theory]
    [InlineData("other-tool", "tenant-a", "{\"assignmentStatus\":\"unassigned\"}")]
    [InlineData("list-phone-numbers", "tenant-b", "{\"assignmentStatus\":\"unassigned\"}")]
    [InlineData("list-phone-numbers", "tenant-a", "{\"assignmentStatus\":\"assigned\"}")]
    public void Validate_RejectsCrossContextTokenUse(string toolId, string tenantId, string filtersJson)
    {
        var service = CreateService();
        var token = service.Issue(
            "list-phone-numbers",
            "tenant-a",
            ParseJson("""{"assignmentStatus":"unassigned"}"""),
            100,
            Now);

        var validation = service.Validate(token, toolId, tenantId, ParseJson(filtersJson), Now.AddMinutes(1));

        Assert.False(validation.IsValid);
        Assert.Null(validation.NextOffset);
        Assert.Equal("invalidContinuationToken", validation.ErrorCode);
    }

    [Fact]
    public void Validate_TreatsExactExpiryBoundaryAsExpired()
    {
        var service = CreateService();
        var filters = ParseJson("{}");
        var token = service.Issue("list-phone-numbers", "tenant-a", filters, 100, Now);

        var validation = service.Validate(
            token,
            "list-phone-numbers",
            "tenant-a",
            filters,
            Now.AddMinutes(30));

        Assert.False(validation.IsValid);
        Assert.Equal("expiredContinuationToken", validation.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("invalid.token.extra")]
    [InlineData("%%%.%%%")]
    public void Validate_RejectsMalformedToken(string token)
    {
        var validation = CreateService().Validate(
            token,
            "list-phone-numbers",
            "tenant-a",
            ParseJson("{}"),
            Now);

        Assert.False(validation.IsValid);
        Assert.Equal("invalidContinuationToken", validation.ErrorCode);
    }

    [Fact]
    public void Validate_RejectsTamperedSignature()
    {
        var service = CreateService();
        var filters = ParseJson("{}");
        var token = service.Issue("list-phone-numbers", "tenant-a", filters, 100, Now);
        var tamperedToken = $"{token[..^1]}{(token[^1] == 'A' ? 'B' : 'A')}";

        var validation = service.Validate(
            tamperedToken,
            "list-phone-numbers",
            "tenant-a",
            filters,
            Now.AddMinutes(1));

        Assert.False(validation.IsValid);
        Assert.Equal("invalidContinuationToken", validation.ErrorCode);
    }

    [Fact]
    public void Issue_RejectsNegativeOffset()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateService().Issue("list-phone-numbers", "tenant-a", ParseJson("{}"), -1, Now));

        Assert.Equal("nextOffset", exception.ParamName);
    }

    private static ContinuationTokenService CreateService() =>
        new(Key, TimeSpan.FromMinutes(30));

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}