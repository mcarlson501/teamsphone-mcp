using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;

namespace TeamsPhoneMcp.Core.Sessions;

public sealed class TenantSessionManager : ITenantSessionManager, IAsyncDisposable
{
    private readonly ITenantSessionFactory _sessionFactory;
    private readonly TenantSessionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantSessionManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, SessionEntry> _sessions = [];
    private readonly CancellationTokenSource _shutdown = new();
    private long _accessOrder;
    private bool _stopping;
    private int _disposeStarted;

    public TenantSessionManager(
        ITenantSessionFactory sessionFactory,
        IOptions<TenantSessionOptions> options,
        TimeProvider timeProvider,
        ILogger<TenantSessionManager> logger)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sessionFactory = sessionFactory;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        TenantSessionContext context,
        TenantOperationKind operationKind,
        Func<ITenantExecutionSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        if (!Enum.IsDefined(operationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(operationKind));
        }

        var lease = await AcquireLeaseAsync(context, cancellationToken);
        if (lease.RequiresInitialization)
        {
            _ = InitializeEntryAsync(lease.Entry, lease.EvictedEntry);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);

        try
        {
            ITenantExecutionSession session;
            try
            {
                session = await lease.Entry.Ready.Task.WaitAsync(linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (
                linkedCancellation.IsCancellationRequested &&
                !lease.Entry.Ready.Task.IsCompleted)
            {
                throw;
            }
            catch
            {
                await InvalidateAsync(lease.Entry);
                throw;
            }

            using var operationLock = operationKind switch
            {
                TenantOperationKind.Read =>
                    await lease.Entry.OperationLock.ReaderLockAsync(linkedCancellation.Token),
                TenantOperationKind.Write =>
                    await lease.Entry.OperationLock.WriterLockAsync(linkedCancellation.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(operationKind))
            };

            try
            {
                return await operation(session, linkedCancellation.Token);
            }
            catch (TenantSessionFatalException)
            {
                await InvalidateAsync(lease.Entry);
                throw;
            }
        }
        finally
        {
            await ReleaseLeaseAsync(lease.Entry);
        }
    }

    public async Task<int> EvictIdleSessionsAsync(CancellationToken cancellationToken = default)
    {
        List<SessionEntry> expired;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_stopping)
            {
                return 0;
            }

            var now = _timeProvider.GetUtcNow();
            expired = _sessions.Values
                .Where(entry =>
                    entry.ActiveOperations == 0 &&
                    !entry.Invalidated &&
                    entry.Ready.Task.IsCompletedSuccessfully &&
                    now - entry.LastUsedUtc >= _options.IdleTimeout)
                .ToList();

            foreach (var entry in expired)
            {
                _sessions.Remove(entry.Context.TenantId);
            }
        }
        finally
        {
            _gate.Release();
        }

        await Task.WhenAll(expired.Select(DisposeEntryAsync));
        return expired.Count;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        List<SessionEntry> entries;

        await _gate.WaitAsync();
        try
        {
            _stopping = true;
            _shutdown.Cancel();
            entries = _sessions.Values.ToList();
            _sessions.Clear();
        }
        finally
        {
            _gate.Release();
        }

        await Task.WhenAll(entries.Select(DrainAndDisposeEntryAsync));
        _shutdown.Dispose();
        _gate.Dispose();
    }

    private async Task<SessionLease> AcquireLeaseAsync(
        TenantSessionContext context,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_stopping)
            {
                throw new ObjectDisposedException(nameof(TenantSessionManager));
            }

            if (_sessions.TryGetValue(context.TenantId, out var existing))
            {
                if (!string.Equals(
                        existing.Context.CredentialRef,
                        context.CredentialRef,
                        StringComparison.Ordinal))
                {
                    throw new TenantSessionException(
                        "credentialContextMismatch",
                        "The tenant already has a session with a different credential reference.");
                }

                if (existing.Invalidated)
                {
                    throw new TenantSessionException(
                        "tenantSessionUnavailable",
                        "The tenant session is being replaced after a fatal failure.");
                }

                Reserve(existing);
                return new SessionLease(existing, RequiresInitialization: false, EvictedEntry: null);
            }

            SessionEntry? evicted = null;
            if (_sessions.Count >= _options.MaxSessions)
            {
                evicted = _sessions.Values
                    .Where(entry =>
                        entry.ActiveOperations == 0 &&
                        entry.Ready.Task.IsCompletedSuccessfully)
                    .OrderBy(entry => entry.LastAccessOrder)
                    .FirstOrDefault();

                if (evicted is null)
                {
                    throw new TenantSessionException(
                        "tenantSessionCapacityExceeded",
                        "All tenant session slots are active. Retry after an operation completes.");
                }

                _sessions.Remove(evicted.Context.TenantId);
            }

            var entry = new SessionEntry(
                context,
                _timeProvider.GetUtcNow(),
                ++_accessOrder);
            Reserve(entry);
            _sessions.Add(context.TenantId, entry);
            return new SessionLease(entry, RequiresInitialization: true, evicted);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Reserve(SessionEntry entry)
    {
        if (entry.ActiveOperations == 0)
        {
            entry.Drained = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        entry.ActiveOperations++;
    }

    private async Task InitializeEntryAsync(SessionEntry entry, SessionEntry? evictedEntry)
    {
        try
        {
            if (evictedEntry is not null)
            {
                await DisposeEntryAsync(evictedEntry);
            }

            var session = await _sessionFactory.CreateAsync(entry.Context, _shutdown.Token);
            if (session is null)
            {
                throw new InvalidOperationException("The tenant session factory returned null.");
            }

            if (session.Context != entry.Context)
            {
                await session.DisposeAsync();
                throw new TenantSessionException(
                    "tenantSessionIdentityMismatch",
                    "The tenant session factory returned a session for a different context.");
            }

            entry.Ready.TrySetResult(session);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            entry.Ready.TrySetCanceled(_shutdown.Token);
        }
        catch (Exception exception)
        {
            entry.Ready.TrySetException(exception);
        }
    }

    private async Task InvalidateAsync(SessionEntry entry)
    {
        await _gate.WaitAsync();
        try
        {
            entry.Invalidated = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReleaseLeaseAsync(SessionEntry entry)
    {
        var shouldDispose = false;
        TaskCompletionSource? drained = null;

        await _gate.WaitAsync();
        try
        {
            entry.ActiveOperations--;
            if (entry.ActiveOperations < 0)
            {
                throw new InvalidOperationException("Tenant session lease count became negative.");
            }

            if (entry.ActiveOperations == 0)
            {
                entry.LastUsedUtc = _timeProvider.GetUtcNow();
                entry.LastAccessOrder = ++_accessOrder;
                drained = entry.Drained;

                if (entry.Invalidated &&
                    _sessions.TryGetValue(entry.Context.TenantId, out var current) &&
                    ReferenceEquals(current, entry))
                {
                    _sessions.Remove(entry.Context.TenantId);
                    shouldDispose = true;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        // Signal drain completion only after releasing the gate. DisposeAsync keys
        // its teardown — including disposing this semaphore — off the drained signal,
        // so signaling while still holding/releasing the gate races the semaphore's
        // disposal and can surface as ObjectDisposedException on _gate.Release().
        drained?.TrySetResult();

        if (shouldDispose)
        {
            await DisposeEntryAsync(entry);
        }
    }

    private async Task DrainAndDisposeEntryAsync(SessionEntry entry)
    {
        await entry.Drained.Task;
        await DisposeEntryAsync(entry);
    }

    private async Task DisposeEntryAsync(SessionEntry entry)
    {
        if (Interlocked.Exchange(ref entry.DisposeStarted, 1) != 0)
        {
            return;
        }

        ITenantExecutionSession session;
        try
        {
            session = await entry.Ready.Task;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            return;
        }

        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to dispose tenant session for tenant {TenantId}.",
                entry.Context.TenantId);
        }
    }

    private sealed class SessionEntry(
        TenantSessionContext context,
        DateTimeOffset lastUsedUtc,
        long lastAccessOrder)
    {
        public TenantSessionContext Context { get; } = context;

        public AsyncReaderWriterLock OperationLock { get; } = new();

        public TaskCompletionSource<ITenantExecutionSession> Ready { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Drained { get; set; } = null!;

        public int ActiveOperations { get; set; }

        public bool Invalidated { get; set; }

        public DateTimeOffset LastUsedUtc { get; set; } = lastUsedUtc;

        public long LastAccessOrder { get; set; } = lastAccessOrder;

        public int DisposeStarted;
    }

    private sealed record SessionLease(
        SessionEntry Entry,
        bool RequiresInitialization,
        SessionEntry? EvictedEntry);
}