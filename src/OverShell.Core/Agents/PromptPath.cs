using System.Text.RegularExpressions;

namespace OverShell.Core.Agents;

/// <summary>
/// PowerShell keeps its location in its session state, not in the process's current
/// directory (<c>Set-Location C:\x</c> leaves <c>[Environment]::CurrentDirectory</c> where
/// the process started), so the PEB tells nothing about a <c>pwsh</c> tab. What is on
/// screen does: the default prompt prints <c>PS C:\path&gt; </c>. This reads the path off
/// the last prompt line when nothing better (OSC 7 / 9;9) is available. Default prompt
/// only, by design; a custom prompt should announce the directory instead
/// (<c>OverShell integrations install shell</c>).
/// </summary>
public static partial class PromptPath
{
    // "PS C:\Users\me> ", "PS C:\> ", "PS \\server\share\dir>> " (nested prompt). Provider
    // paths (HKLM:\..., Microsoft.PowerShell.Core\FileSystem::...) are not file paths and
    // are left alone. Anything typed after the prompt is tolerated: the row is the prompt
    // plus whatever the user has started typing.
    [GeneratedRegex(@"^PS (?<path>(?:[A-Za-z]:\\[^>\r\n]*?|\\\\[^>\r\n]+?))>+(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultPrompt { get; }

    /// <summary>The directory named by the last prompt line in <paramref name="rows"/>, bottom up; null when no row looks like one.</summary>
    public static string? FromScreen(IReadOnlyList<string> rows)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            if (row.Length == 0)
            {
                continue;
            }

            var match = DefaultPrompt.Match(row);
            if (match.Success)
            {
                var path = match.Groups["path"].Value.TrimEnd();
                return path.Length > 3 ? path.TrimEnd('\\') : path;
            }

            // The first non-empty row from the bottom that is not a prompt is output or a
            // command still running; the prompt above it is stale. Stop looking.
            return null;
        }

        return null;
    }
}
