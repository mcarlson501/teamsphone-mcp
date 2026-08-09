using System.Collections.Concurrent;

namespace TeamsPhoneMcp.Core.Policy;

/// <summary>
/// Remembers the <c>jti</c> of every confirmation token that has been redeemed, so a
/// token cannot be replayed for the remainder of its TTL (build spec §15 S2).
/// Entries are kept only until the token they identify would have expired anyway, so
/// the cache never needs to hold more than one TTL worth of writes.
/// </summary>
public sealed class ConsumedConfirmationTokenCache
{
    /// <summary>
    /// Ceiling on retained identifiers. Per-session rate limiting (30 calls/minute) against a
    /// 15-minute TTL bounds a well-behaved deployment two orders of magnitude below this.
    /// </summary>
    public const int DefaultCapacity = 100_000;

    private readonly ConcurrentDictionary<string, long> _consumed = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private long _lastSweepUnixSeconds;

    public ConsumedConfirmationTokenCache(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _capacity = capacity;
    }

    public int Count => _consumed.Count;

    /// <summary>
    /// Atomically records <paramref name="jti"/> as redeemed. Returns <c>false</c> when the
    /// identifier was already consumed, or when the cache is full and cannot record the
    /// redemption — in both cases the caller must reject the token, because a redemption
    /// this class cannot remember is a redemption it cannot prevent from being replayed.
    /// </summary>
    public ConsumedTokenResult TryConsume(string jti, long expiresAtUnixSeconds, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);

        Sweep(nowUtc);

        if (_consumed.Count >= _capacity)
        {
            return ConsumedTokenResult.CacheFull;
        }

        return _consumed.TryAdd(jti, expiresAtUnixSeconds)
            ? ConsumedTokenResult.Consumed
            : ConsumedTokenResult.AlreadyConsumed;
    }

    /// <summary>Drops identifiers whose tokens have expired, at most once per second.</summary>
    private void Sweep(DateTimeOffset nowUtc)
    {
        var nowUnixSeconds = nowUtc.ToUnixTimeSeconds();
        var lastSweep = Interlocked.Read(ref _lastSweepUnixSeconds);
        if (nowUnixSeconds <= lastSweep && _consumed.Count < _capacity)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastSweepUnixSeconds, nowUnixSeconds, lastSweep) != lastSweep)
        {
            return;
        }

        foreach (var (jti, expiresAt) in _consumed)
        {
            if (expiresAt <= nowUnixSeconds)
            {
                _consumed.TryRemove(jti, out _);
            }
        }
    }
}

public enum ConsumedTokenResult
{
    Consumed,
    AlreadyConsumed,
    CacheFull,
}
