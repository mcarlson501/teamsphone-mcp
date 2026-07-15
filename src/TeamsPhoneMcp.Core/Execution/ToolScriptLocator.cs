namespace TeamsPhoneMcp.Core.Execution;

/// <summary>
/// Resolves the <c>run.ps1</c> path for a tool from its id, rooted at the tools
/// directory. Guards against path traversal by requiring the resolved path to
/// stay inside the tools root, so a malformed id can never reach an arbitrary
/// script on disk.
/// </summary>
public sealed class ToolScriptLocator
{
    public const string ScriptFileName = "run.ps1";

    private readonly string _toolsRootPath;

    public ToolScriptLocator(string toolsRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRootPath);
        _toolsRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(toolsRootPath));
    }

    /// <summary>The absolute, normalized tools root directory.</summary>
    public string ToolsRootPath => _toolsRootPath;

    /// <summary>
    /// Resolves the <c>run.ps1</c> path for <paramref name="toolId"/>. Returns
    /// <see langword="false"/> when the id is blank, the resolved path escapes the
    /// tools root, or the script file does not exist.
    /// </summary>
    public bool TryResolve(string toolId, out string scriptPath)
    {
        scriptPath = string.Empty;
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(_toolsRootPath, toolId, ScriptFileName));
        var rootPrefix = _toolsRootPath + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!File.Exists(candidate))
        {
            return false;
        }

        scriptPath = candidate;
        return true;
    }
}
