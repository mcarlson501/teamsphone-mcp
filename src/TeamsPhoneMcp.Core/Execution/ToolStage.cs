namespace TeamsPhoneMcp.Core.Execution;

/// <summary>
/// Ordered execution stages a tool script may implement, mirroring the
/// <c>-Stage</c> ValidateSet in <c>tools/&lt;name&gt;/run.ps1</c> (build spec §6.2).
/// </summary>
public enum ToolStage
{
    /// <summary>Capture pre-execution state used for diffing and rollback.</summary>
    Snapshot,

    /// <summary>Validate preconditions without mutating tenant state.</summary>
    Preflight,

    /// <summary>Produce a simulated result without mutating tenant state.</summary>
    DryRun,

    /// <summary>Apply the real change (or, for read tools, perform the read).</summary>
    Execute,

    /// <summary>Prove the change succeeded.</summary>
    Verify,

    /// <summary>Undo a failed change using the captured snapshot.</summary>
    Rollback
}
