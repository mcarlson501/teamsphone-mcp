using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TeamsPhoneMcp.Core.Manifests;

public sealed class ManifestValidatingMcpServerTool(McpServerTool innerTool)
    : DelegatingMcpServerTool(innerTool)
{
    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var services = request.Services
            ?? throw new InvalidOperationException("The MCP tool request does not provide a service provider.");
        var manifestCatalog = services.GetRequiredService<IToolManifestCatalog>();
        var manifest = manifestCatalog.GetRequired(ProtocolTool.Name);
        ToolArgumentValidator.Validate(manifest, request.Params?.Arguments);

        return base.InvokeAsync(request, cancellationToken);
    }
}