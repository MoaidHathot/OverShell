namespace OverShell.Core.Agents;

/// <summary>How a tab's directory can be learned from the process that owns the prompt.</summary>
public enum CwdSource
{
    /// <summary>Read the process's current directory (PEB): cmd, bash, zsh, nu… keep it current.</summary>
    Process,

    /// <summary>
    /// PowerShell: its location lives in session state, the process directory never moves.
    /// Read the prompt line off the screen instead (<see cref="PromptPath"/>).
    /// </summary>
    Prompt,
}

/// <summary>
/// Which process in a tab speaks for "where the tab is" (DESIGN.md §12.14). The shell's
/// directory is what the user means by it — <c>cd foo; opencode</c> leaves the shell in
/// <c>foo</c> and the agent starts there — so the deepest known shell wins: a nested
/// <c>cmd</c> inside <c>pwsh</c> is where the prompt is. A tab whose root is not a shell
/// (a profile that runs the agent itself) is asked directly.
/// </summary>
public static class ShellCwdTarget
{
    /// <summary>Image names (no extension) that are shells, any case.</summary>
    public static readonly IReadOnlySet<string> ShellImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pwsh", "powershell", "cmd", "bash", "sh", "zsh", "fish", "nu", "elvish", "xonsh", "wsl", "git-bash", "busybox",
    };

    private static readonly HashSet<string> PowerShellImages = new(StringComparer.OrdinalIgnoreCase) { "pwsh", "powershell" };

    public static bool IsShell(string image) => ShellImages.Contains(image);

    public static bool IsPowerShell(string image) => PowerShellImages.Contains(image);

    /// <summary>
    /// The pid to ask and how: the deepest shell in <paramref name="descendants"/>
    /// (breadth-first order, nearest first, as <c>ProcessTree</c> returns them), else the
    /// root (<paramref name="rootImage"/> says what it is).
    /// </summary>
    public static (uint Pid, string Image, CwdSource Source) Pick(uint rootPid, string rootImage, IReadOnlyList<(uint Pid, string Image)> descendants)
    {
        var chosen = (Pid: rootPid, Image: rootImage);
        foreach (var entry in descendants)
        {
            if (IsShell(entry.Image))
            {
                chosen = entry;
            }
        }

        return (chosen.Pid, chosen.Image, IsPowerShell(chosen.Image) ? CwdSource.Prompt : CwdSource.Process);
    }
}
