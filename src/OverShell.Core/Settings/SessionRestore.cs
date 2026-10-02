using OverShell.Core.Agents;

namespace OverShell.Core.Settings;

/// <summary>How a restored tab gets its agent back.</summary>
public enum ResumeMode
{
    /// <summary>Nothing to do: no agent was running, resuming is off, or there is no safe way.</summary>
    None,

    /// <summary>Type the command into the shell once it is quiet (a shell profile that had the agent started inside it).</summary>
    Typed,

    /// <summary>Start the profile's program with the resume arguments appended (a profile whose program is the agent itself).</summary>
    Relaunch,
}

/// <summary>The decision for one tab, with the reason for the explain trail.</summary>
public sealed record ResumePlan(ResumeMode Mode, string? Command, string Reason)
{
    /// <summary>True when the command is the harness's "most recent session" form rather than a session by id.</summary>
    public bool UsesMostRecent { get; init; }

    public static ResumePlan None(string reason) => new(ResumeMode.None, null, reason);
}

/// <summary>
/// Decides how a restored tab picks up where its agent left off (DESIGN.md §12.13). Pure:
/// the saved tab, the profile's command line, the rules and the settings go in, a plan
/// comes out. Two things it gets right that typing blindly did not: a profile whose
/// program <em>is</em> the agent (<c>opencode.exe</c>) must be relaunched with the resume
/// arguments — typing <c>opencode --session …</c> into a running OpenCode lands in its
/// prompt box as text — and an agent without a known session id can still come back
/// through its "most recent session" form when the user allows it.
/// </summary>
public static class SessionRestore
{
    public const string SessionIdPlaceholder = "{sessionId}";

    /// <summary>
    /// Plans every tab of a restore at once, with one rule a single-tab plan cannot apply:
    /// the "most recent session" form is used <em>at most once per harness</em>. OpenCode's
    /// <c>--continue</c>, tried from an empty directory, reopened the latest session of
    /// another project — the tool's notion is machine-wide — so two id-less OpenCode tabs
    /// would both land in the same session. The active tab gets it when it qualifies,
    /// else the first in order; the rest of that harness come back with their directory.
    /// </summary>
    public static ResumePlan[] PlanAll(IReadOnlyList<SavedTab> tabs, int activeIndex, Func<SavedTab, string?> profileCommandLineFor, AgentRules rules, SessionSettings settings)
    {
        var plans = new ResumePlan[tabs.Count];
        var order = Enumerable.Range(0, tabs.Count).OrderBy(i => i == activeIndex ? 0 : 1).ThenBy(i => i).ToArray();
        var lastUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var i in order)
        {
            var plan = Plan(tabs[i], profileCommandLineFor(tabs[i]), rules, settings);
            if (plan.Mode != ResumeMode.None && plan.UsesMostRecent && tabs[i].Harness is { } harness && !lastUsed.Add(harness))
            {
                plan = ResumePlan.None($"another {tabs[i].Harness} tab already resumes the most recent session");
            }

            plans[i] = plan;
        }

        return plans;
    }

    public static ResumePlan Plan(SavedTab tab, string? profileCommandLine, AgentRules rules, SessionSettings settings)
    {
        if (!settings.ResumeAgents)
        {
            return ResumePlan.None("session.resumeAgents is off");
        }

        if (!tab.AgentRunning)
        {
            return ResumePlan.None("no agent was running");
        }

        var ruleSet = rules.Find(tab.Harness);
        var (command, source, mostRecent) = ResumeCommandFor(tab, ruleSet, settings);
        if (command is null)
        {
            return ResumePlan.None(tab.SessionId is null && !settings.ResumeWithoutId
                ? "no session id and session.resumeWithoutId is off"
                : "no resume command known for this harness");
        }

        var profileHarness = rules.DetectFromCommandline(profileCommandLine);
        if (profileHarness is null)
        {
            // A shell: the agent was started by hand inside it, so the resume is typed the same way.
            return new ResumePlan(ResumeMode.Typed, command, $"typed into the shell ({source})") { UsesMostRecent = mostRecent };
        }

        if (!string.Equals(profileHarness, tab.Harness, StringComparison.OrdinalIgnoreCase))
        {
            return ResumePlan.None($"profile launches {profileHarness}, tab ran {tab.Harness}");
        }

        var programHarness = rules.DetectFromCommandline(FirstToken(profileCommandLine));
        if (programHarness is null)
        {
            // `pwsh -NoExit -Command opencode`: the agent is an argument of a shell that exits
            // with it; neither typing nor relaunching is safe, so the directory is all that comes back.
            return ResumePlan.None("profile wraps the agent in a shell command");
        }

        var resumeProgram = FirstToken(command);
        if (!string.Equals(rules.DetectFromCommandline(resumeProgram), tab.Harness, StringComparison.OrdinalIgnoreCase))
        {
            return ResumePlan.None($"resume command '{command}' does not start with the harness program");
        }

        var arguments = command[resumeProgram.Length..].Trim();
        var relaunch = arguments.Length == 0 ? profileCommandLine!.Trim() : $"{profileCommandLine!.Trim()} {arguments}";
        return new ResumePlan(ResumeMode.Relaunch, relaunch, $"relaunched with resume arguments ({source})") { UsesMostRecent = mostRecent };
    }

    /// <summary>The command and where it came from: the saved one, the rule's pattern with the id, or the rule's "most recent" form.</summary>
    private static (string? Command, string Source, bool MostRecent) ResumeCommandFor(SavedTab tab, AgentRuleSet? ruleSet, SessionSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(tab.ResumeCommand))
        {
            return (tab.ResumeCommand.Trim(), "saved resume command", false);
        }

        if (!string.IsNullOrWhiteSpace(tab.SessionId) && ruleSet?.ResumeCommand is { } pattern && pattern.Contains(SessionIdPlaceholder, StringComparison.Ordinal))
        {
            return (pattern.Replace(SessionIdPlaceholder, tab.SessionId, StringComparison.Ordinal), "rule pattern with the saved session id", false);
        }

        if (settings.ResumeWithoutId && !string.IsNullOrWhiteSpace(ruleSet?.ResumeLastCommand))
        {
            return (ruleSet.ResumeLastCommand.Trim(), "most recent session, no id known", true);
        }

        return (null, string.Empty, false);
    }

    /// <summary>The program of a command line: the first quoted or whitespace-delimited token, quotes kept.</summary>
    public static string FirstToken(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        var text = commandLine.TrimStart();
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? text : text[..(close + 1)];
        }

        var end = text.IndexOfAny([' ', '\t']);
        return end < 0 ? text : text[..end];
    }
}
