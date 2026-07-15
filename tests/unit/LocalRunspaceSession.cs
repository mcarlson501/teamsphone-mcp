using System.Management.Automation.Runspaces;
using TeamsPhoneMcp.Core.Sessions;

namespace TeamsPhoneMcp.UnitTests;

/// <summary>
/// Test-only <see cref="IPowerShellTenantSession"/> backed by a plain, local
/// runspace. It performs no authentication and imports no MicrosoftTeams module,
/// so it exercises the real PowerShell execution path fully offline without any
/// tenant. A production session (a later milestone) additionally connects the
/// runspace to a tenant.
/// </summary>
internal sealed class LocalRunspaceSession : IPowerShellTenantSession
{
    private readonly Runspace _runspace;

    public LocalRunspaceSession(TenantSessionContext context)
    {
        Context = context;
        _runspace = RunspaceFactory.CreateRunspace();
        _runspace.Open();
    }

    public TenantSessionContext Context { get; }

    public Runspace Runspace => _runspace;

    public ValueTask DisposeAsync()
    {
        _runspace.Dispose();
        return ValueTask.CompletedTask;
    }
}
