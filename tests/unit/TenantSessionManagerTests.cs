using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsPhoneMcp.Core.Sessions;

namespace TeamsPhoneMcp.UnitTests;

public class TenantSessionManagerTests
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid TenantC = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset StartTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public async Task ExecuteAsync_InterleavedTenantsNeverShareSessions()
    {
        var factory = new RecordingSessionFactory();
        await using var manager = CreateManager(factory);

        var calls = Enumerable.Range(0, 20)
            .Select(index =>
            {
                var context = index % 2 == 0 ? Context(TenantA) : Context(TenantB);
                return manager.ExecuteAsync(
                    context,
                    TenantOperationKind.Read,
                    (session, _) => Task.FromResult((context.TenantId, Session: (TestSession)session)));
            });

        var results = await Task.WhenAll(calls);
        var tenantASessions = results
            .Where(result => result.TenantId == TenantA)
            .Select(result => result.Session.Id)
            .Distinct()
            .ToArray();
        var tenantBSessions = results
            .Where(result => result.TenantId == TenantB)
            .Select(result => result.Session.Id)
            .Distinct()
            .ToArray();

        Assert.Single(tenantASessions);
        Assert.Single(tenantBSessions);
        Assert.NotEqual(tenantASessions[0], tenantBSessions[0]);
        Assert.All(results, result => Assert.Equal(result.TenantId, result.Session.Context.TenantId));
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentFirstCallsCreateOneSession()
    {
        var creationStarted = NewSignal();
        var allowCreation = NewSignal();
        var factory = new RecordingSessionFactory(async (context, cancellationToken) =>
        {
            creationStarted.TrySetResult();
            await allowCreation.Task.WaitAsync(cancellationToken);
            return new TestSession(context);
        });
        await using var manager = CreateManager(factory);

        var calls = Enumerable.Range(0, 10)
            .Select(_ => manager.ExecuteAsync(
                Context(TenantA),
                TenantOperationKind.Read,
                (session, _) => Task.FromResult(((TestSession)session).Id)))
            .ToArray();

        await creationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.CreateCount);

        allowCreation.SetResult();
        var sessionIds = await Task.WhenAll(calls);

        Assert.Single(sessionIds.Distinct());
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsCredentialChangeForLiveTenant()
    {
        var factory = new RecordingSessionFactory();
        await using var manager = CreateManager(factory);
        await manager.ExecuteAsync(
            Context(TenantA, "credential-a"),
            TenantOperationKind.Read,
            static (_, _) => Task.FromResult(true));

        var exception = await Assert.ThrowsAsync<TenantSessionException>(() =>
            manager.ExecuteAsync(
                Context(TenantA, "credential-b"),
                TenantOperationKind.Read,
                static (_, _) => Task.FromResult(true)));

        Assert.Equal("credentialContextMismatch", exception.ErrorCode);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReadsCanOverlap()
    {
        var factory = new RecordingSessionFactory();
        await using var manager = CreateManager(factory);
        var bothEntered = NewSignal();
        var release = NewSignal();
        var activeReaders = 0;

        async Task<bool> ReadAsync(ITenantExecutionSession _, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref activeReaders) == 2)
            {
                bothEntered.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return true;
        }

        var first = manager.ExecuteAsync(Context(TenantA), TenantOperationKind.Read, ReadAsync);
        var second = manager.ExecuteAsync(Context(TenantA), TenantOperationKind.Read, ReadAsync);

        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task ExecuteAsync_WritesAreExclusive()
    {
        var factory = new RecordingSessionFactory();
        await using var manager = CreateManager(factory);
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var secondEntered = NewSignal();

        var first = manager.ExecuteAsync(
            Context(TenantA),
            TenantOperationKind.Write,
            async (_, cancellationToken) =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return true;
            });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = manager.ExecuteAsync(
            Context(TenantA),
            TenantOperationKind.Write,
            (_, _) =>
            {
                secondEntered.SetResult();
                return Task.FromResult(true);
            });

        await AssertRemainsBlockedAsync(secondEntered.Task);
        releaseFirst.SetResult();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(first, second);
    }

    [Theory]
    [InlineData(TenantOperationKind.Read, TenantOperationKind.Write)]
    [InlineData(TenantOperationKind.Write, TenantOperationKind.Read)]
    public async Task ExecuteAsync_ReadsAndWritesCannotOverlap(
        TenantOperationKind firstKind,
        TenantOperationKind secondKind)
    {
        var factory = new RecordingSessionFactory();
        await using var manager = CreateManager(factory);
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var secondEntered = NewSignal();

        var first = manager.ExecuteAsync(
            Context(TenantA),
            firstKind,
            async (_, cancellationToken) =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return true;
            });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = manager.ExecuteAsync(
            Context(TenantA),
            secondKind,
            (_, _) =>
            {
                secondEntered.SetResult();
                return Task.FromResult(true);
            });

        await AssertRemainsBlockedAsync(secondEntered.Task);
        releaseFirst.SetResult();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task EvictIdleSessionsAsync_ExpiresAtExactBoundary()
    {
        var timeProvider = new ManualTimeProvider(StartTime);
        var factory = new RecordingSessionFactory();
        await using var manager = CreateManager(factory, timeProvider: timeProvider);
        await manager.ExecuteAsync(
            Context(TenantA),
            TenantOperationKind.Read,
            static (_, _) => Task.FromResult(true));
        var firstSession = Assert.Single(factory.Sessions);

        timeProvider.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        Assert.Equal(0, await manager.EvictIdleSessionsAsync());
        Assert.Equal(0, firstSession.DisposeCount);

        timeProvider.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, await manager.EvictIdleSessionsAsync());
        Assert.Equal(1, firstSession.DisposeCount);

        await manager.ExecuteAsync(
            Context(TenantA),
            TenantOperationKind.Read,
            static (_, _) => Task.FromResult(true));
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task Capacity_DoesNotExpireOrEvictActiveSession()
    {
        var timeProvider = new ManualTimeProvider(StartTime);
        var factory = new RecordingSessionFactory();
        await using var manager = CreateManager(
            factory,
            new TenantSessionOptions { MaxSessions = 1 },
            timeProvider);
        var entered = NewSignal();
        var release = NewSignal();

        var activeCall = manager.ExecuteAsync(
            Context(TenantA),
            TenantOperationKind.Read,
            async (_, cancellationToken) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return true;
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, await manager.EvictIdleSessionsAsync());
        var exception = await Assert.ThrowsAsync<TenantSessionException>(() =>
            manager.ExecuteAsync(
                Context(TenantB),
                TenantOperationKind.Read,
                static (_, _) => Task.FromResult(true)));
        Assert.Equal("tenantSessionCapacityExceeded", exception.ErrorCode);

        release.SetResult();
        await activeCall;
        await manager.ExecuteAsync(
            Context(TenantB),
            TenantOperationKind.Read,
            static (_, _) => Task.FromResult(true));

        Assert.Equal(1, factory.Sessions.Single(session => session.Context.TenantId == TenantA).DisposeCount);
    }

    [Fact]
    public async Task Capacity_EvictsLeastRecentlyUsedInactiveSession()
    {
        var timeProvider = new ManualTimeProvider(StartTime);
        var factory = new RecordingSessionFactory();
        await using var manager = CreateManager(
            factory,
            new TenantSessionOptions { MaxSessions = 2 },
            timeProvider);

        await TouchAsync(manager, TenantA);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await TouchAsync(manager, TenantB);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await TouchAsync(manager, TenantA);
        await TouchAsync(manager, TenantC);

        var tenantA = factory.Sessions.Single(session => session.Context.TenantId == TenantA);
        var tenantB = factory.Sessions.Single(session => session.Context.TenantId == TenantB);
        Assert.Equal(0, tenantA.DisposeCount);
        Assert.Equal(1, tenantB.DisposeCount);
        Assert.Equal(3, factory.CreateCount);
    }

    [Fact]
    public async Task ExecuteAsync_CreationFailureCanBeRetried()
    {
        var attempt = 0;
        var factory = new RecordingSessionFactory((context, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                throw new InvalidOperationException("Synthetic creation failure.");
            }

            return Task.FromResult(new TestSession(context));
        });
        await using var manager = CreateManager(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ExecuteAsync(
                Context(TenantA),
                TenantOperationKind.Read,
                static (_, _) => Task.FromResult(true)));

        var result = await manager.ExecuteAsync(
            Context(TenantA),
            TenantOperationKind.Read,
            static (_, _) => Task.FromResult(true));

        Assert.True(result);
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task ExecuteAsync_FatalFailureDisposesAndReplacesSession()
    {
        var factory = new RecordingSessionFactory();
        await using var manager = CreateManager(factory);
        Guid failedSessionId = Guid.Empty;

        var exception = await Assert.ThrowsAsync<TenantSessionFatalException>(() =>
            manager.ExecuteAsync<bool>(
                Context(TenantA),
                TenantOperationKind.Read,
                (session, _) =>
                {
                    failedSessionId = ((TestSession)session).Id;
                    throw new TenantSessionFatalException("runspaceCorrupted", "Synthetic fatal failure.");
                }));

        Assert.Equal("runspaceCorrupted", exception.ErrorCode);
        var failedSession = factory.Sessions.Single(session => session.Id == failedSessionId);
        Assert.Equal(1, failedSession.DisposeCount);

        var replacementId = await manager.ExecuteAsync(
            Context(TenantA),
            TenantOperationKind.Read,
            (session, _) => Task.FromResult(((TestSession)session).Id));
        Assert.NotEqual(failedSessionId, replacementId);
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task ExecuteAsync_OrdinaryFailurePreservesSession()
    {
        var factory = new RecordingSessionFactory();
        await using var manager = CreateManager(factory);
        Guid failedSessionId = Guid.Empty;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ExecuteAsync<bool>(
                Context(TenantA),
                TenantOperationKind.Read,
                (session, _) =>
                {
                    failedSessionId = ((TestSession)session).Id;
                    throw new InvalidOperationException("Synthetic tool failure.");
                }));

        var nextSessionId = await manager.ExecuteAsync(
            Context(TenantA),
            TenantOperationKind.Read,
            (session, _) => Task.FromResult(((TestSession)session).Id));

        Assert.Equal(failedSessionId, nextSessionId);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancellationPreservesSession()
    {
        var factory = new RecordingSessionFactory();
        await using var manager = CreateManager(factory);
        using var cancellation = new CancellationTokenSource();
        var entered = NewSignal();

        var canceledCall = manager.ExecuteAsync<bool>(
            Context(TenantA),
            TenantOperationKind.Read,
            async (_, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            },
            cancellation.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);

        var nextSessionId = await manager.ExecuteAsync(
            Context(TenantA),
            TenantOperationKind.Read,
            (session, _) => Task.FromResult(((TestSession)session).Id));
        Assert.Equal(factory.Sessions.Single().Id, nextSessionId);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, factory.Sessions.Single().DisposeCount);
    }

    [Fact]
    public async Task ExecuteAsync_DisposesFactorySessionWithWrongIdentity()
    {
        TestSession? wrongSession = null;
        var factory = new RecordingSessionFactory((_, _) =>
        {
            wrongSession = new TestSession(Context(TenantB));
            return Task.FromResult(wrongSession);
        });
        await using var manager = CreateManager(factory);

        var exception = await Assert.ThrowsAsync<TenantSessionException>(() =>
            manager.ExecuteAsync(
                Context(TenantA),
                TenantOperationKind.Read,
                static (_, _) => Task.FromResult(true)));

        Assert.Equal("tenantSessionIdentityMismatch", exception.ErrorCode);
        Assert.NotNull(wrongSession);
        Assert.Equal(1, wrongSession.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_CancelsActiveWorkAndDisposesAllSessionsOnce()
    {
        var factory = new RecordingSessionFactory();
        var manager = CreateManager(factory);
        var entered = NewSignal();

        var activeCall = manager.ExecuteAsync<bool>(
            Context(TenantA),
            TenantOperationKind.Read,
            async (_, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await TouchAsync(manager, TenantB);

        var dispose = manager.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activeCall);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, factory.CreateCount);
        Assert.All(factory.Sessions, session => Assert.Equal(1, session.DisposeCount));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.ExecuteAsync(
                Context(TenantC),
                TenantOperationKind.Read,
                static (_, _) => Task.FromResult(true)));
    }

    private static TenantSessionContext Context(Guid tenantId, string credentialRef = "test-credential") =>
        new(tenantId, credentialRef);

    private static TenantSessionManager CreateManager(
        RecordingSessionFactory factory,
        TenantSessionOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(
            factory,
            Options.Create(options ?? new TenantSessionOptions()),
            timeProvider ?? new ManualTimeProvider(StartTime),
            NullLogger<TenantSessionManager>.Instance);

    private static async Task TouchAsync(TenantSessionManager manager, Guid tenantId)
    {
        await manager.ExecuteAsync(
            Context(tenantId),
            TenantOperationKind.Read,
            static (_, _) => Task.FromResult(true));
    }

    private static async Task AssertRemainsBlockedAsync(Task signal)
    {
        var timeout = Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Same(timeout, await Task.WhenAny(signal, timeout));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class RecordingSessionFactory : ITenantSessionFactory
    {
        private readonly Func<TenantSessionContext, CancellationToken, Task<TestSession>> _create;
        private int _createCount;

        public RecordingSessionFactory(
            Func<TenantSessionContext, CancellationToken, Task<TestSession>>? create = null)
        {
            _create = create ?? ((context, _) => Task.FromResult(new TestSession(context)));
        }

        public int CreateCount => Volatile.Read(ref _createCount);

        public ConcurrentBag<TestSession> Sessions { get; } = [];

        public async ValueTask<ITenantExecutionSession> CreateAsync(
            TenantSessionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createCount);
            var session = await _create(context, cancellationToken);
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class TestSession(TenantSessionContext context) : ITenantExecutionSession
    {
        private int _disposeCount;

        public Guid Id { get; } = Guid.NewGuid();

        public TenantSessionContext Context { get; } = context;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}