namespace OverShell.Core.Integrations;

/// <summary>What <see cref="ShellLaunch.Inject"/> decided for one command line.</summary>
/// <param name="CommandLine">The command line to run - changed only when <paramref name="Injected"/>.</param>
/// <param name="Injected">Whether the shell integration script rides along.</param>
/// <param name="Reason">Why not, in a few words, for the explain panel; null when injected.</param>
public sealed record ShellLaunchPlan(string CommandLine, bool Injected, string? Reason);

/// <summary>
/// Puts the shell integration into a PowerShell command line without touching any profile
/// (DESIGN.md §12.15) - the way VS Code does it: <c>-NoExit -Command ". '&lt;script&gt;'"</c>
/// appended to a plain <c>pwsh</c> / <c>powershell</c> launch. The profile still runs first
/// (profiles run unless <c>-NoProfile</c>), so the script wraps whatever prompt the user
/// ends up with - starship, oh-my-posh, the default - in the right order.
/// <para>
/// Only an interactive shell launch is touched. A command line that already carries a
/// command, a file or an encoded command - or a positional script path - is the user's
/// business and passes through unchanged; so does any other program. PowerShell accepts
/// unique prefixes of its parameter names (<c>-c</c>, <c>-com</c>, <c>-f</c>, <c>-e</c>),
/// which is why the switches are matched by prefix and an ambiguous prefix counts as a
/// command.
/// </para>
/// </summary>
public static class ShellLaunch
{
    /// <summary>
    /// Switches that mean "run this, not an interactive session", as PowerShell resolves
    /// them: a prefix of the full name no shorter than its smallest unambiguous form
    /// (<c>-c</c>, <c>-co</c>, <c>-com</c> are all <c>-Command</c>), plus the short aliases.
    /// </summary>
    private static readonly (string Name, int MinLength)[] CommandSwitches =
        [("command", 1), ("file", 1), ("encodedcommand", 1), ("encodedarguments", 8)];

    private static readonly string[] CommandAliases = ["ec", "ea"];

    /// <summary>Switches that consume the next token as their value; that token is not a script path.</summary>
    private static readonly (string Name, int MinLength)[] ValueSwitches =
    [
        ("executionpolicy", 2), ("workingdirectory", 16), ("configurationname", 6), ("configurationfile", 17),
        ("inputformat", 3), ("outputformat", 1), ("windowstyle", 1), ("psconsolefile", 3),
        ("settingsfile", 8), ("custompipename", 6),
    ];

    private static readonly string[] ValueAliases = ["wd", "ep", "if", "of"];

    /// <param name="commandLine">The profile's command line, environment already expanded.</param>
    /// <param name="scriptPath">The integration script on disk.</param>
    public static ShellLaunchPlan Inject(string commandLine, string scriptPath)
    {
        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0)
        {
            return new ShellLaunchPlan(commandLine, false, "empty command line");
        }

        var programTokens = ProgramTokenCount(tokens);
        var program = ProgramName(string.Join(' ', tokens.Take(programTokens)));
        if (program is not ("pwsh" or "powershell"))
        {
            return new ShellLaunchPlan(commandLine, false, $"{program} is not PowerShell");
        }

        var hasNoExit = false;
        for (var i = programTokens; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Length > 0 && token[0] is '-' or '/')
            {
                var name = token[1..].ToLowerInvariant();
                if (name is "noexit" or "noe")
                {
                    hasNoExit = true;
                    continue;
                }

                if (name.Length == 0 || name == "-")
                {
                    // "-" reads the command from stdin; "--" makes everything after it positional.
                    return new ShellLaunchPlan(commandLine, false, "the profile runs a command");
                }

                if (Matches(name, CommandSwitches) || CommandAliases.Contains(name))
                {
                    return new ShellLaunchPlan(commandLine, false, "the profile runs a command");
                }

                if (Matches(name, ValueSwitches) || ValueAliases.Contains(name))
                {
                    i++;
                }

                continue;
            }

            // A bare token after the program is a script path (pwsh script.ps1) or a command.
            return new ShellLaunchPlan(commandLine, false, "the profile runs a command");
        }

        var escaped = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        var suffix = (hasNoExit ? string.Empty : " -NoExit") + $" -Command \"try {{ . '{escaped}' }} catch {{ }}\"";
        return new ShellLaunchPlan(commandLine.TrimEnd() + suffix, true, null);
    }

    private static bool Matches(string name, (string Name, int MinLength)[] switches) =>
        switches.Any(s => name.Length >= s.MinLength && s.Name.StartsWith(name, StringComparison.Ordinal));

    /// <summary>
    /// How many leading tokens make up the program. An unquoted path with spaces is legal -
    /// <c>CreateProcess</c> tries ever longer prefixes until one is a file - so the program is
    /// the shortest run of tokens ending in <c>.exe</c>, stopping at the first switch; else the
    /// first token alone.
    /// </summary>
    private static int ProgramTokenCount(IReadOnlyList<string> tokens)
    {
        if (tokens[0].StartsWith('"'))
        {
            return 1;
        }

        for (var count = 1; count <= tokens.Count; count++)
        {
            var token = tokens[count - 1];
            if (count > 1 && token.Length > 0 && token[0] is '-' or '/')
            {
                break;
            }

            if (token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return count;
            }
        }

        return 1;
    }
    /// <summary>The program's name without directory, quotes or extension, lower-case.</summary>
    public static string ProgramName(string token)
    {
        var text = token.Trim().Trim('"');
        var slash = text.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            text = text[(slash + 1)..];
        }

        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        return text.ToLowerInvariant();
    }

    /// <summary>Whitespace-separated tokens, double quotes grouping (kept on the token).</summary>
    public static IReadOnlyList<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                current.Append(ch);
                any = true;
            }
            else if (!quoted && char.IsWhiteSpace(ch))
            {
                if (any)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(ch);
                any = true;
            }
        }

        if (any)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
