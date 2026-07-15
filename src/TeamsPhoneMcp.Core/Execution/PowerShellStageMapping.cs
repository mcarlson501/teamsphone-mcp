namespace TeamsPhoneMcp.Core.Execution;

/// <summary>
/// Maps <see cref="ToolStage"/> values to the lowercase <c>-Stage</c> tokens the
/// tool scripts declare in their <c>[ValidateSet(...)]</c> (build spec §6.2).
/// Uses an explicit switch rather than <see cref="Enum.ToString()"/> because the
/// enum casing (e.g. <c>DryRun</c>) differs from the script token (<c>dryrun</c>).
/// </summary>
internal static class PowerShellStageMapping
{
    public static string ToScriptToken(ToolStage stage) => stage switch
    {
        ToolStage.Snapshot => "snapshot",
        ToolStage.Preflight => "preflight",
        ToolStage.DryRun => "dryrun",
        ToolStage.Execute => "execute",
        ToolStage.Verify => "verify",
        ToolStage.Rollback => "rollback",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown tool stage."),
    };
}
