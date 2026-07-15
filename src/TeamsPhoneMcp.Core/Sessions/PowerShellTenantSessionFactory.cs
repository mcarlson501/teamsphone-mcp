using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamsPhoneMcp.Core.Execution;
using TeamsPhoneMcp.Credentials;

namespace TeamsPhoneMcp.Core.Sessions;

/// <summary>
/// Production tenant session factory (build spec §4.1). Resolves the session's
/// named <c>credentialRef</c> to an Entra app-only certificate, then creates an
/// in-process PowerShell runspace connected to that one tenant. Any failure
/// disposes the partially-built session and surfaces a fatal error so the
/// manager never reuses a possibly-corrupt session.
/// </summary>
public sealed class PowerShellTenantSessionFactory : ITenantSessionFactory
{
    private readonly ICredentialProvider _credentialProvider;
    private readonly PowerShellTenantConnectionOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PowerShellTenantSessionFactory> _logger;

    public PowerShellTenantSessionFactory(
        ICredentialProvider credentialProvider,
        IOptions<PowerShellTenantConnectionOptions> options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(credentialProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _credentialProvider = credentialProvider;
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PowerShellTenantSessionFactory>();
    }

    public async ValueTask<ITenantExecutionSession> CreateAsync(
        TenantSessionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        TenantCredential credential;
        try
        {
            credential = await _credentialProvider.ResolveAsync(context.CredentialRef, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CredentialResolutionException ex)
        {
            _logger.LogWarning(ex, "Could not resolve credential for a tenant session.");
            throw new TenantSessionFatalException(
                StageErrorCodes.AuthenticationFailed,
                "The tenant credential could not be resolved.");
        }

        // Invariant: a credential must belong to the tenant it is used for.
        if (credential.TenantId != context.TenantId)
        {
            credential.Certificate.Dispose();
            throw new TenantSessionFatalException(
                StageErrorCodes.AuthenticationFailed,
                "The resolved credential does not belong to the requested tenant.");
        }

        return await PowerShellTenantSession
            .ConnectAsync(context, credential, _options, _loggerFactory.CreateLogger<PowerShellTenantSession>(), cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// A live, single-tenant PowerShell session. Owns the runspace lifecycle:
/// creation, MicrosoftTeams module import, <c>Connect-MicrosoftTeams</c>, and
/// disconnect/dispose. The stage executor only borrows the connected runspace.
/// </summary>
internal sealed class PowerShellTenantSession : IPowerShellTenantSession
{
    private readonly Runspace _runspace;
    private readonly PowerShellTenantConnectionOptions _options;
    private readonly ILogger<PowerShellTenantSession> _logger;

    private PowerShellTenantSession(
        TenantSessionContext context,
        Runspace runspace,
        PowerShellTenantConnectionOptions options,
        ILogger<PowerShellTenantSession> logger)
    {
        Context = context;
        _runspace = runspace;
        _options = options;
        _logger = logger;
    }

    public TenantSessionContext Context { get; }

    public Runspace Runspace => _runspace;

    public static async Task<PowerShellTenantSession> ConnectAsync(
        TenantSessionContext context,
        TenantCredential credential,
        PowerShellTenantConnectionOptions options,
        ILogger<PowerShellTenantSession> logger,
        CancellationToken cancellationToken)
    {
        var runspace = RunspaceFactory.CreateRunspace();
        try
        {
            runspace.Open();

            await ImportModuleAsync(runspace, options, cancellationToken).ConfigureAwait(false);
            await ConnectAsync(runspace, context, credential, options, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Opened a Teams session for tenant {TenantId}.", context.TenantId);
            return new PowerShellTenantSession(context, runspace, options, logger);
        }
        catch (OperationCanceledException)
        {
            runspace.Dispose();
            throw;
        }
        catch (TenantSessionFatalException)
        {
            runspace.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            runspace.Dispose();
            logger.LogError(ex, "Failed to open a Teams session for tenant {TenantId}.", context.TenantId);
            throw new TenantSessionFatalException(
                StageErrorCodes.AuthenticationFailed,
                "The tenant session could not be established.");
        }
        finally
        {
            // The private key is retained by the connected runspace; drop our handle.
            credential.Certificate.Dispose();
        }
    }

    private static async Task ImportModuleAsync(
        Runspace runspace,
        PowerShellTenantConnectionOptions options,
        CancellationToken cancellationToken)
    {
        using var powerShell = PowerShell.Create();
        powerShell.Runspace = runspace;
        powerShell.AddCommand("Import-Module").AddParameter("Name", options.ModuleName);
        if (!string.IsNullOrWhiteSpace(options.ModuleRequiredVersion))
        {
            powerShell.AddParameter("RequiredVersion", options.ModuleRequiredVersion);
        }

        await InvokeAndThrowOnErrorAsync(powerShell, "import the Teams module", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ConnectAsync(
        Runspace runspace,
        TenantSessionContext context,
        TenantCredential credential,
        PowerShellTenantConnectionOptions options,
        CancellationToken cancellationToken)
    {
        using var powerShell = PowerShell.Create();
        powerShell.Runspace = runspace;

        // App-only sign-in with the resolved certificate object. Passing the
        // X509Certificate2 directly (rather than -CertificateThumbprint) works for
        // both certificates loaded from an OS store and those loaded from a .pfx
        // file, so it is portable across Windows and macOS/Linux hosts (see docs).
        powerShell
            .AddCommand("Connect-MicrosoftTeams")
            .AddParameter("TenantId", context.TenantId.ToString())
            .AddParameter("ApplicationId", credential.ClientId)
            .AddParameter("Certificate", credential.Certificate);

        await InvokeAndThrowOnErrorAsync(powerShell, "connect to the tenant", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task InvokeAndThrowOnErrorAsync(
        PowerShell powerShell,
        string actionDescription,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var asyncResult = powerShell.BeginInvoke();
        await using (cancellationToken.Register(static state => ((PowerShell)state!).Stop(), powerShell).ConfigureAwait(false))
        {
            try
            {
                await Task.Factory.FromAsync(asyncResult, powerShell.EndInvoke).ConfigureAwait(false);
            }
            catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        if (powerShell.HadErrors || powerShell.Streams.Error.Count > 0)
        {
            throw new TenantSessionFatalException(
                StageErrorCodes.AuthenticationFailed,
                $"Failed to {actionDescription}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_runspace.RunspaceStateInfo.State == RunspaceState.Opened)
            {
                using var powerShell = PowerShell.Create();
                powerShell.Runspace = _runspace;
                powerShell.AddCommand("Disconnect-MicrosoftTeams");
                await Task.Factory
                    .FromAsync(powerShell.BeginInvoke(), powerShell.EndInvoke)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Best-effort disconnect; the runspace is disposed regardless.
            _logger.LogDebug(ex, "Disconnect-MicrosoftTeams failed during session disposal.");
        }
        finally
        {
            _runspace.Dispose();
        }
    }
}
