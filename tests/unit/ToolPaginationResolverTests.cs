using System.Text.Json;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Core.Manifests;
using TeamsPhoneMcp.Core.Policy;

namespace TeamsPhoneMcp.UnitTests;

public class ToolPaginationResolverTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void Resolve_UsesDefaultsForFirstPage()
    {
        var resolution = CreateResolver().Resolve(
            Manifest(),
            "tenant-a",
            ParseArguments("{}"),
            ParseJson("""{"assignmentStatus":"unassigned"}"""),
            Now);

        Assert.True(resolution.IsValid);
        Assert.Equal(100, resolution.Pagination!.PageSize);
        Assert.Equal(0, resolution.Pagination.Offset);
        Assert.Null(resolution.ErrorCode);
    }

    [Fact]
    public void Resolve_UsesRequestedPageSizeAndValidatedOffset()
    {
        var filters = ParseJson("""{"assignmentStatus":"unassigned"}""");
        var tokenService = CreateTokenService();
        var token = tokenService.Issue("list-phone-numbers", "tenant-a", filters, 125, Now);
        var arguments = ParseArguments($$"""{"pageSize":50,"continuationToken":"{{token}}"}""");

        var resolution = new ToolPaginationResolver(tokenService).Resolve(
            Manifest(),
            "tenant-a",
            arguments,
            filters,
            Now.AddMinutes(1));

        Assert.True(resolution.IsValid);
        Assert.Equal(50, resolution.Pagination!.PageSize);
        Assert.Equal(125, resolution.Pagination.Offset);
    }

    [Fact]
    public void Resolve_RejectsTokenWhenFiltersChange()
    {
        var tokenService = CreateTokenService();
        var token = tokenService.Issue(
            "list-phone-numbers",
            "tenant-a",
            ParseJson("""{"assignmentStatus":"unassigned"}"""),
            100,
            Now);
        var arguments = ParseArguments($$"""{"continuationToken":"{{token}}"}""");

        var resolution = new ToolPaginationResolver(tokenService).Resolve(
            Manifest(),
            "tenant-a",
            arguments,
            ParseJson("""{"assignmentStatus":"assigned"}"""),
            Now.AddMinutes(1));

        Assert.False(resolution.IsValid);
        Assert.Null(resolution.Pagination);
        Assert.Equal("invalidContinuationToken", resolution.ErrorCode);
    }

    [Fact]
    public void Resolve_RejectsOversizedPageSizeWhenCalledDirectly()
    {
        var resolution = CreateResolver().Resolve(
            Manifest(),
            "tenant-a",
            ParseArguments("""{"pageSize":201}"""),
            ParseJson("{}"),
            Now);

        Assert.False(resolution.IsValid);
        Assert.Equal("invalidPagination", resolution.ErrorCode);
    }

    [Fact]
    public void Resolve_ReturnsNoPaginationForDetailTool()
    {
        var manifest = Manifest() with
        {
            Inputs = new Dictionary<string, ToolManifestInput>(StringComparer.Ordinal)
        };

        var resolution = CreateResolver().Resolve(
            manifest,
            "tenant-a",
            ParseArguments("{}"),
            ParseJson("{}"),
            Now);

        Assert.True(resolution.IsValid);
        Assert.Null(resolution.Pagination);
    }

    [Fact]
    public void IssueContinuationToken_ReturnsResolverConsumableToken()
    {
        var resolver = CreateResolver();
        var manifest = Manifest();
        var filters = ParseJson("""{"assignmentStatus":"unassigned"}""");
        var token = resolver.IssueContinuationToken(manifest, "tenant-a", filters, 200, Now);
        var arguments = ParseArguments($$"""{"continuationToken":"{{token}}"}""");

        var resolution = resolver.Resolve(manifest, "tenant-a", arguments, filters, Now.AddMinutes(1));

        Assert.True(resolution.IsValid);
        Assert.Equal(200, resolution.Pagination!.Offset);
    }

    private static ToolPaginationResolver CreateResolver() => new(CreateTokenService());

    private static ContinuationTokenService CreateTokenService() =>
        new(Key, TimeSpan.FromMinutes(30));

    private static ToolManifest Manifest() => new()
    {
        Id = "list-phone-numbers",
        Version = "1.0.0",
        Summary = "List phone numbers.",
        Category = "read",
        RiskTier = 0,
        Annotations = new ToolManifestAnnotations { ReadOnlyHint = true, IdempotentHint = true },
        Inputs = new Dictionary<string, ToolManifestInput>(StringComparer.Ordinal)
        {
            ["pageSize"] = new() { Type = "integer", Required = false, Minimum = 1, Maximum = 200 },
            ["continuationToken"] = new() { Type = "string", Required = false }
        },
        MaxBlastRadius = 0,
        TimeoutSeconds = 30
    };

    private static IReadOnlyDictionary<string, JsonElement> ParseArguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}