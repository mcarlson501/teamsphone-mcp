using Microsoft.Extensions.Logging.Abstractions;
using TeamsPhoneMcp.Core.Manifests;

namespace TeamsPhoneMcp.UnitTests;

public class ToolManifestCatalogTests
{
    [Fact]
    public void Catalog_LoadsMockWriteManifest_FromToolsFolder()
    {
        var toolsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools"));
        var catalog = new ToolManifestCatalog(toolsRoot, NullLogger<ToolManifestCatalog>.Instance);

        var manifest = catalog.GetRequired("mock-write-user-policy");

        Assert.Equal(2, manifest.RiskTier);
        Assert.Equal(1, manifest.MaxBlastRadius);
        Assert.False(manifest.Annotations.ReadOnlyHint);

        var ping = catalog.GetRequired("ping");
        Assert.Equal(0, ping.RiskTier);
        Assert.Equal(0, ping.MaxBlastRadius);
        Assert.True(ping.Annotations.ReadOnlyHint);
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenIdDoesNotMatchFolderName()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"manifest-tests-{Guid.NewGuid():N}");
        var toolFolder = Path.Combine(tempRoot, "different-folder");
        Directory.CreateDirectory(toolFolder);
        File.WriteAllText(
            Path.Combine(toolFolder, "manifest.yaml"),
            """
            id: some-other-id
            version: 1.0.0
            summary: test
            category: read
            riskTier: 0
            annotations:
              readOnlyHint: true
              destructiveHint: false
              idempotentHint: true
            inputs:
              tenantId: { type: string, required: true }
            maxBlastRadius: 0
            timeoutSeconds: 30
            """);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => _ = new ToolManifestCatalog(tempRoot, NullLogger<ToolManifestCatalog>.Instance));
            Assert.Contains("must match its folder name", ex.Message);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenInputsSectionIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"manifest-tests-{Guid.NewGuid():N}");
        var toolFolder = Path.Combine(tempRoot, "no-inputs-tool");
        Directory.CreateDirectory(toolFolder);
        File.WriteAllText(
            Path.Combine(toolFolder, "manifest.yaml"),
            """
            id: no-inputs-tool
            version: 1.0.0
            summary: test
            category: read
            riskTier: 0
            annotations:
              readOnlyHint: true
              destructiveHint: false
              idempotentHint: true
            maxBlastRadius: 0
            timeoutSeconds: 30
            """);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => _ = new ToolManifestCatalog(tempRoot, NullLogger<ToolManifestCatalog>.Instance));
            Assert.Contains("missing required field 'inputs'", ex.Message);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenRiskTierKeyIsMissing()
    {
        // riskTier: 0 is a valid, meaningful value (skips the dry-run/confirmation-token
        // gate), so an omitted key must be rejected rather than silently defaulting to it.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"manifest-tests-{Guid.NewGuid():N}");
        var toolFolder = Path.Combine(tempRoot, "no-risk-tier-tool");
        Directory.CreateDirectory(toolFolder);
        File.WriteAllText(
            Path.Combine(toolFolder, "manifest.yaml"),
            """
            id: no-risk-tier-tool
            version: 1.0.0
            summary: test
            category: read
            annotations:
              readOnlyHint: true
              destructiveHint: false
              idempotentHint: true
            inputs:
              tenantId: { type: string, required: true }
            maxBlastRadius: 0
            timeoutSeconds: 30
            """);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => _ = new ToolManifestCatalog(tempRoot, NullLogger<ToolManifestCatalog>.Instance));
            Assert.Contains("missing required field 'riskTier'", ex.Message);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenAnnotationsSectionIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"manifest-tests-{Guid.NewGuid():N}");
        var toolFolder = Path.Combine(tempRoot, "no-annotations-tool");
        Directory.CreateDirectory(toolFolder);
        File.WriteAllText(
            Path.Combine(toolFolder, "manifest.yaml"),
            """
            id: no-annotations-tool
            version: 1.0.0
            summary: test
            category: read
            riskTier: 0
            inputs:
              tenantId: { type: string, required: true }
            maxBlastRadius: 0
            timeoutSeconds: 30
            """);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => _ = new ToolManifestCatalog(tempRoot, NullLogger<ToolManifestCatalog>.Instance));
            Assert.Contains("missing required field 'annotations'", ex.Message);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenInputTypeIsMissing()
    {
        var yaml = CreateReadManifest("bad-input-tool")
            .Replace("    type: string\n", string.Empty, StringComparison.Ordinal);

        var exception = AssertInvalidManifest("bad-input-tool", yaml);

        Assert.Contains("missing required field 'type'", exception.Message);
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenYamlContainsUnknownField()
    {
        var exception = AssertInvalidManifest(
            "strict-tool",
            CreateWriteManifest("strict-tool") + "unexpectedField: true\n");

        Assert.Contains("unexpectedField", exception.Message);
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenWriteCategoryUsesTierZero()
    {
        var yaml = CreateWriteManifest("zero-risk-write").Replace("riskTier: 1", "riskTier: 0", StringComparison.Ordinal);

        var exception = AssertInvalidManifest("zero-risk-write", yaml);

        Assert.Contains("write tools must use riskTier 1, 2, or 3", exception.Message);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("exec")]
    [InlineData("invoke-command")]
    public void Catalog_RejectsManifest_WhenToolIdIsGenericExecutionName(string toolId)
    {
        var exception = AssertInvalidManifest(toolId, CreateWriteManifest(toolId));

        Assert.Contains("Unsafe tool id", exception.Message);
    }

    [Fact]
    public void Catalog_AllowsPlannedRunTenantHealthCheckToolId()
    {
        const string toolId = "run-tenant-health-check";
        var manifest = LoadSingleManifest(toolId, CreateReadManifest(toolId));

        Assert.Equal(toolId, manifest.Id);
        Assert.Equal(0, manifest.RiskTier);
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenInputFormatIsUnsupported()
    {
        var yaml = CreateWriteManifest("bad-format-tool")
            .Replace("    required: true", "    required: true\n    format: phone", StringComparison.Ordinal);

        var exception = AssertInvalidManifest("bad-format-tool", yaml);

        Assert.Contains("unsupported format 'phone'", exception.Message);
    }

    [Fact]
    public void Catalog_LoadsInputConstraintsAndRedactedParams()
    {
        var yaml = CreateReadManifest("constrained-tool")
            .Replace(
                "    required: false",
                "    required: false\n    allowedValues: [assigned, unassigned]\n  batchSize:\n    type: integer\n    required: false\n    minimum: 1\n    maximum: 200",
                StringComparison.Ordinal)
            .Replace("maxBlastRadius: 0", "redactParams: [target]\nmaxBlastRadius: 0", StringComparison.Ordinal);

        var manifest = LoadSingleManifest("constrained-tool", yaml);

        Assert.Equal(["assigned", "unassigned"], manifest.Inputs["target"].AllowedValues);
        Assert.Equal(1, manifest.Inputs["batchSize"].Minimum);
        Assert.Equal(200, manifest.Inputs["batchSize"].Maximum);
        Assert.Equal(["target"], manifest.RedactParams);
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenAllowedValuesAreDeclaredForNonStringInput()
    {
        var yaml = CreateReadManifest("bad-allowed-values")
            .Replace(
                "    type: string\n    required: false",
                "    type: integer\n    required: false\n    allowedValues: [one, two]",
                StringComparison.Ordinal);

        var exception = AssertInvalidManifest("bad-allowed-values", yaml);

        Assert.Contains("allowedValues can only be used with string inputs", exception.Message);
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenNumericBoundsAreInvalid()
    {
        var yaml = CreateReadManifest("bad-bounds")
            .Replace(
                "    type: string\n    required: false",
                "    type: integer\n    required: false\n    minimum: 200\n    maximum: 1",
                StringComparison.Ordinal);

        var exception = AssertInvalidManifest("bad-bounds", yaml);

        Assert.Contains("minimum cannot exceed maximum", exception.Message);
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenRedactedParamIsNotAnInput()
    {
        var yaml = CreateReadManifest("bad-redaction")
            .Replace("maxBlastRadius: 0", "redactParams: [secret]\nmaxBlastRadius: 0", StringComparison.Ordinal);

        var exception = AssertInvalidManifest("bad-redaction", yaml);

        Assert.Contains("redactParams entry 'secret' does not match an input", exception.Message);
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenPaginationInputsAreNotDeclaredTogether()
    {
        var yaml = CreateReadManifest("incomplete-pagination")
            .Replace(
                "maxBlastRadius: 0",
                "  pageSize:\n    type: integer\n    required: false\n    minimum: 1\n    maximum: 200\nmaxBlastRadius: 0",
                StringComparison.Ordinal);

        var exception = AssertInvalidManifest("incomplete-pagination", yaml);

        Assert.Contains("must declare both pageSize and continuationToken", exception.Message);
    }

    [Fact]
    public void Catalog_RejectsManifest_WhenPageSizeDoesNotUseStandardBounds()
    {
        var yaml = CreateReadManifest("bad-page-size")
            .Replace(
                "maxBlastRadius: 0",
                "  pageSize:\n    type: integer\n    required: false\n    minimum: 1\n    maximum: 500\n  continuationToken: { type: string, required: false }\nmaxBlastRadius: 0",
                StringComparison.Ordinal);

        var exception = AssertInvalidManifest("bad-page-size", yaml);

        Assert.Contains("pageSize must be an optional integer between 1 and 200", exception.Message);
    }

    private static ToolManifest LoadSingleManifest(string toolId, string yaml)
    {
        var tempRoot = CreateManifestDirectory(toolId, yaml);
        try
        {
            var catalog = new ToolManifestCatalog(tempRoot, NullLogger<ToolManifestCatalog>.Instance);
            return catalog.GetRequired(toolId);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static InvalidOperationException AssertInvalidManifest(string toolId, string yaml)
    {
        var tempRoot = CreateManifestDirectory(toolId, yaml);
        try
        {
            return Assert.Throws<InvalidOperationException>(
                () => _ = new ToolManifestCatalog(tempRoot, NullLogger<ToolManifestCatalog>.Instance));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateManifestDirectory(string toolId, string yaml)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"manifest-tests-{Guid.NewGuid():N}");
        var toolFolder = Path.Combine(tempRoot, toolId);
        Directory.CreateDirectory(toolFolder);
        File.WriteAllText(Path.Combine(toolFolder, "manifest.yaml"), yaml);
        return tempRoot;
    }

    private static string CreateWriteManifest(string toolId) =>
        $"""
        id: {toolId}
        version: 1.0.0
        summary: test write tool
        category: change
        riskTier: 1
        annotations:
          readOnlyHint: false
          destructiveHint: false
          idempotentHint: true
        inputs:
          target:
            type: string
            required: true
        maxBlastRadius: 1
        timeoutSeconds: 30
        """ + Environment.NewLine;

    private static string CreateReadManifest(string toolId) =>
        $"""
        id: {toolId}
        version: 1.0.0
        summary: test read tool
        category: read
        riskTier: 0
        annotations:
          readOnlyHint: true
          destructiveHint: false
          idempotentHint: true
        inputs:
          target:
            type: string
            required: false
        maxBlastRadius: 0
        timeoutSeconds: 30
        """ + Environment.NewLine;
}
