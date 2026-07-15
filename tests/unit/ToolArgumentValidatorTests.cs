using System.Text.Json;
using ModelContextProtocol;
using TeamsPhoneMcp.Core.Manifests;

namespace TeamsPhoneMcp.UnitTests;

public class ToolArgumentValidatorTests
{
    [Fact]
    public void Validate_AcceptsArgumentsThatMatchManifest()
    {
        var arguments = ParseArguments("""
            {
              "targetUserUpn": "user@example.com",
              "blastRadius": 1,
              "dryRun": true,
              "score": 1.5
            }
            """);

        ToolArgumentValidator.Validate(CreateManifest(), arguments);
    }

    [Fact]
    public void Validate_RejectsMissingRequiredArgument()
    {
        var exception = Assert.Throws<McpException>(
            () => ToolArgumentValidator.Validate(CreateManifest(), null));

        Assert.Contains("missing required argument 'targetUserUpn'", exception.Message);
    }

    [Fact]
    public void Validate_RejectsUnknownArgument()
    {
        var arguments = ParseArguments("""
            {
              "targetUserUpn": "user@example.com",
              "script": "Invoke-Anything"
            }
            """);

        var exception = Assert.Throws<McpException>(
            () => ToolArgumentValidator.Validate(CreateManifest(), arguments));

        Assert.Contains("unknown argument 'script'", exception.Message);
        Assert.DoesNotContain("Invoke-Anything", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsBlankRequiredString(string upn)
    {
        var arguments = ParseArguments($$"""
            {
              "targetUserUpn": "{{upn}}"
            }
            """);

        var exception = Assert.Throws<McpException>(
            () => ToolArgumentValidator.Validate(CreateManifest(), arguments));

        Assert.Contains("argument 'targetUserUpn' must not be empty", exception.Message);
    }

    [Theory]
    [InlineData("\"1\"", "blastRadius")]
    [InlineData("1.5", "blastRadius")]
    [InlineData("\"true\"", "dryRun")]
    [InlineData("true", "score")]
    [InlineData("null", "comment")]
    public void Validate_RejectsIncorrectJsonType(string jsonValue, string inputName)
    {
        var arguments = ParseArguments($$"""
            {
              "targetUserUpn": "user@example.com",
              "{{inputName}}": {{jsonValue}}
            }
            """);

        var exception = Assert.Throws<McpException>(
            () => ToolArgumentValidator.Validate(CreateManifest(), arguments));

        Assert.Contains($"argument '{inputName}' must be", exception.Message);
    }

    [Theory]
    [InlineData("missing-at-sign")]
    [InlineData("two@@example.com")]
    [InlineData("user name@example.com")]
    public void Validate_RejectsInvalidUpn(string upn)
    {
        var arguments = ParseArguments($$"""
            {
              "targetUserUpn": "{{upn}}"
            }
            """);

        var exception = Assert.Throws<McpException>(
            () => ToolArgumentValidator.Validate(CreateManifest(), arguments));

        Assert.Contains("argument 'targetUserUpn' must be a valid UPN", exception.Message);
        Assert.DoesNotContain(upn, exception.Message);
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseArguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }

    private static ToolManifest CreateManifest() => new()
    {
        Id = "test-tool",
        Version = "1.0.0",
        Summary = "Test manifest",
        Category = "change",
        RiskTier = 1,
        Annotations = new ToolManifestAnnotations(),
        Inputs = new Dictionary<string, ToolManifestInput>(StringComparer.Ordinal)
        {
            ["targetUserUpn"] = new() { Type = "string", Format = "upn", Required = true },
            ["blastRadius"] = new() { Type = "integer", Required = false },
            ["dryRun"] = new() { Type = "boolean", Required = false },
            ["comment"] = new() { Type = "string", Required = false },
            ["score"] = new() { Type = "number", Required = false }
        },
        MaxBlastRadius = 1,
        TimeoutSeconds = 30
    };
}