using System.Management.Automation.Runspaces;

namespace TeamsPhoneMcp.Core.Sessions;

/// <summary>
/// A tenant session that exposes an in-process PowerShell runspace for stage
/// execution (build spec §4.1, §6.2). The session owns the runspace lifecycle —
/// creation, MicrosoftTeams module import, <c>Connect-MicrosoftTeams</c>, and
/// disposal — while the stage executor merely borrows the already-connected
/// runspace to invoke the tool script. The executor never authenticates,
/// re-keys, or disposes the runspace.
/// </summary>
public interface IPowerShellTenantSession : ITenantExecutionSession
{
    /// <summary>
    /// The open, tenant-connected runspace this session owns. A runspace runs one
    /// pipeline at a time; callers that permit concurrent reads are responsible
    /// for serializing access to it.
    /// </summary>
    Runspace Runspace { get; }
}
