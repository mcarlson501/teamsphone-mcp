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
            maxBlastRadius: 1
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
}
