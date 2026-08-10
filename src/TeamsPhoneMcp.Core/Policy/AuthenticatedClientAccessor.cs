namespace TeamsPhoneMcp.Core.Policy;

/// <summary>
/// The caller identity the host derived from the bearer token on the current request.
/// Populated by the HTTP transport's auth middleware; absent on stdio, which is treated
/// as locally trusted. The client never supplies this value (build spec §15 S2).
/// </summary>
public interface IAuthenticatedClientAccessor
{
    string? ClientId { get; }

    /// <summary>
    /// Whether the operator configured this client so that it can only ever simulate writes.
    /// Unlike the session ceiling the client asks for at initialize, this one cannot be declined,
    /// and it survives transports that issue no session id.
    /// </summary>
    bool WhatIfMode { get; }
}

public sealed class AuthenticatedClientAccessor : IAuthenticatedClientAccessor
{
    // One scope carries both values so they cannot drift apart the way two parallel
    // AsyncLocals set from the same middleware could.
    private readonly AsyncLocal<AuthenticatedClient> _client = new();

    public string? ClientId => _client.Value.ClientId;

    public bool WhatIfMode => _client.Value.WhatIfMode;

    public IDisposable Enter(string? clientId, bool whatIfMode = false)
    {
        var previous = _client.Value;
        _client.Value = new AuthenticatedClient(clientId, whatIfMode);
        return new Scope(() => _client.Value = previous);
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

public readonly record struct AuthenticatedClient(string? ClientId, bool WhatIfMode);
