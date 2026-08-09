using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TeamsPhoneMcp.Core.Policy;

public interface IMcpSessionPolicyStore
{
    bool IsWhatIfMode(string? sessionId);
}

public interface IMcpRequestSessionAccessor
{
    string? SessionId { get; }
}

internal sealed class McpRequestSessionAccessor : IMcpRequestSessionAccessor
{
    private readonly AsyncLocal<string?> _sessionId = new();

    public string? SessionId => _sessionId.Value;

    public IDisposable Enter(string? sessionId)
    {
        var previous = _sessionId.Value;
        _sessionId.Value = sessionId;
        return new Scope(() => _sessionId.Value = previous);
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

internal sealed class McpSessionPolicyStore : IMcpSessionPolicyStore
{
    private readonly ConcurrentDictionary<string, bool> _whatIfModes = new(StringComparer.Ordinal);

    public bool IsWhatIfMode(string? sessionId) =>
        sessionId is not null &&
        _whatIfModes.TryGetValue(sessionId, out var whatIfMode) &&
        whatIfMode;

    public void Initialize(string? sessionId, JsonNode? parameters)
    {
        if (sessionId is null)
        {
            return;
        }

        _whatIfModes.TryAdd(sessionId, ReadWhatIfMode(parameters));
    }

    public static McpMessageFilter CreateInitializationFilter() => next => async (context, cancellationToken) =>
    {
        var requestSessionAccessor = context.Services?.GetService<McpRequestSessionAccessor>();
        using var requestScope = requestSessionAccessor?.Enter(context.Server.SessionId);

        if (context.JsonRpcMessage is JsonRpcRequest { Method: RequestMethods.Initialize } request)
        {
            context.Services?
                .GetService<McpSessionPolicyStore>()?
                .Initialize(context.Server.SessionId, request.Params);
        }

        await next(context, cancellationToken).ConfigureAwait(false);
    };

    private static bool ReadWhatIfMode(JsonNode? parameters)
    {
        if (parameters is not JsonObject value ||
            value["_meta"] is not JsonObject metadata ||
            metadata["whatIfMode"] is null)
        {
            return false;
        }

        if (metadata["whatIfMode"] is JsonValue whatIfMode &&
            whatIfMode.TryGetValue<bool>(out var enabled))
        {
            return enabled;
        }

        throw new ModelContextProtocol.McpException(
            "Initialization metadata 'whatIfMode' must be a boolean.");
    }
}