namespace TeamsPhoneMcp.Core.Policy;

/// <summary>
/// The caller identity the host derived from the bearer token on the current request.
/// Populated by the HTTP transport's auth middleware; absent on stdio, which is treated
/// as locally trusted. The client never supplies this value (build spec §15 S2).
/// </summary>
public interface IAuthenticatedClientAccessor
{
    string? ClientId { get; }
}

public sealed class AuthenticatedClientAccessor : IAuthenticatedClientAccessor
{
    private readonly AsyncLocal<string?> _clientId = new();

    public string? ClientId => _clientId.Value;

    public IDisposable Enter(string? clientId)
    {
        var previous = _clientId.Value;
        _clientId.Value = clientId;
        return new Scope(() => _clientId.Value = previous);
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
