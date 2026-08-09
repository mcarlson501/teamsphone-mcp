using System.Collections.Concurrent;

namespace TeamsPhoneMcp.Core.Policy;

/// <summary>
/// Records which authenticated client opened each MCP session, so a session id observed by
/// one client cannot be presented by another. Caller attribution itself comes from
/// <see cref="IAuthenticatedClientAccessor"/>; this store exists to stop one client from
/// riding another's session (build spec §15 S2).
/// </summary>
public sealed class McpSessionOwnershipStore
{
    /// <summary>Ceiling on tracked sessions; the oldest claims are dropped beyond it.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly ConcurrentDictionary<string, Claim> _claims = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private long _sequence;

    public McpSessionOwnershipStore(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _capacity = capacity;
    }

    /// <summary>
    /// Claims <paramref name="sessionId"/> for <paramref name="clientId"/> on first use.
    /// Returns <c>false</c> if the session is already claimed by a different caller.
    /// </summary>
    public bool TryClaim(string sessionId, string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var candidate = new Claim(clientId, Interlocked.Increment(ref _sequence));
        if (!string.Equals(_claims.GetOrAdd(sessionId, candidate).ClientId, clientId, StringComparison.Ordinal))
        {
            return false;
        }

        Trim();
        return true;
    }

    private void Trim()
    {
        if (_claims.Count <= _capacity)
        {
            return;
        }

        var excess = _claims.Count - _capacity;
        foreach (var (sessionId, _) in _claims.OrderBy(pair => pair.Value.Sequence).Take(excess))
        {
            _claims.TryRemove(sessionId, out _);
        }
    }

    private readonly record struct Claim(string ClientId, long Sequence);
}
