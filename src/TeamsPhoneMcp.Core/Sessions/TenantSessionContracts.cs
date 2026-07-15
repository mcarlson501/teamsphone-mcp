namespace TeamsPhoneMcp.Core.Sessions;

public sealed record TenantSessionContext
{
    public TenantSessionContext(Guid tenantId, string credentialRef)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant ID must not be empty.", nameof(tenantId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(credentialRef);

        TenantId = tenantId;
        CredentialRef = credentialRef.Trim();
    }

    public Guid TenantId { get; }

    public string CredentialRef { get; }
}

public enum TenantOperationKind
{
    Read,
    Write
}

public interface ITenantExecutionSession : IAsyncDisposable
{
    TenantSessionContext Context { get; }
}

public interface ITenantSessionFactory
{
    ValueTask<ITenantExecutionSession> CreateAsync(
        TenantSessionContext context,
        CancellationToken cancellationToken);
}

internal sealed class UnconfiguredTenantSessionFactory : ITenantSessionFactory
{
    public ValueTask<ITenantExecutionSession> CreateAsync(
        TenantSessionContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<ITenantExecutionSession>(
            new TenantSessionException(
                "tenantSessionFactoryUnavailable",
                "Tenant session execution is not configured."));
}

public interface ITenantSessionManager
{
    Task<TResult> ExecuteAsync<TResult>(
        TenantSessionContext context,
        TenantOperationKind operationKind,
        Func<ITenantExecutionSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}

public sealed class TenantSessionException : Exception
{
    public TenantSessionException(string errorCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public sealed class TenantSessionFatalException : Exception
{
    public TenantSessionFatalException(string errorCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}