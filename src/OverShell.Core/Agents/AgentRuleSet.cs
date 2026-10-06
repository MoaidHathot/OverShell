using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OverShell.Core.Agents;

/// <summary>What an agent tab is doing, as far as OverShell can tell.</summary>
public enum AgentState
{
    /// <summary>No signal yet.</summary>
    Unknown,

    /// <summary>Waiting for the user, and the user has seen it.</summary>
    Idle,

    /// <summary>Producing output or reporting a turn in progress.</summary>
    Working,

    /// <summary>Stopped on a question or permission prompt. Strict: never inferred from silence.</summary>
    Blocked,

    /// <summary>Finished a turn while the tab was not being looked at. Stays until viewed.</summary>
    Done,

    /// <summary>The agent reported a failure.</summary>
    Error,

    /// <summary>The session's process has ended.</summary>
    Exited,
}

/// <summary>Who currently decides the state of a tab.</summary>
public enum AgentAuthority
{
    /// <summary>OverShell's own signals: titles, screen text, progress, activity.</summary>
    Detector,

    /// <summary>A harness integration (hook or plugin) is reporting; it wins while it does.</summary>
    Integration,
}

/// <summary>Regex lists that classify a piece of text into a state.</summary>
public sealed class StateRules
{
    public string[] Blocked { get; init; } = [];
    public string[] Working { get; init; } = [];
    public string[] Idle { get; init; } = [];
    public string[] Error { get; init; } = [];
    public string[] Done { get; init; } = [];

    [JsonIgnore]
    internal CompiledStateRules Compiled => _compiled ??= new CompiledStateRules(this);

    private CompiledStateRules? _compiled;

    /// <summary>
    /// The screen row that put the agent in <paramref name="state"/> - the line with the
    /// question, for an inbox to show (§12.17) - or null when no pattern for that state
    /// matches any row. The last matching row wins: prompts sit at the bottom.
    /// </summary>
    public string? MatchingLine(IReadOnlyList<string> rows, AgentState state) => Compiled.MatchingLine(rows, state);
}
internal sealed class CompiledStateRules
{
    private readonly (AgentState State, Regex Regex, string Pattern)[] _rules;

    public CompiledStateRules(StateRules source)
    {
        var list = new List<(AgentState, Regex, string)>();
        Add(list, AgentState.Blocked, source.Blocked);
        Add(list, AgentState.Error, source.Error);
        Add(list, AgentState.Working, source.Working);
        Add(list, AgentState.Done, source.Done);
        Add(list, AgentState.Idle, source.Idle);
        _rules = list.ToArray();
    }

    public bool IsEmpty => _rules.Length == 0;

    /// <summary>The last row any pattern for <paramref name="state"/> matches, trimmed; null when none.</summary>
    public string? MatchingLine(IReadOnlyList<string> rows, AgentState state)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            if (row.Trim().Length == 0)
            {
                continue;
            }

            foreach (var (ruleState, regex, _) in _rules)
            {
                if (ruleState == state && regex.IsMatch(row))
                {
                    return row.Trim();
                }
            }
        }

        return null;
    }
    /// <summary>First match in precedence order (blocked, error, working, done, idle), with the pattern that hit.</summary>
    public (AgentState State, string Pattern)? Match(string text)
    {
        foreach (var (state, regex, pattern) in _rules)
        {
            if (regex.IsMatch(text))
            {
                return (state, pattern);
            }
        }

        return null;
    }

    private static void Add(List<(AgentState, Regex, string)> list, AgentState state, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                list.Add((state, new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, TimeSpan.FromMilliseconds(50)), pattern));
            }
            catch (ArgumentException)
            {
                // A bad user pattern must not take detection down with it.
            }
        }
    }
}

/// <summary>How to recognise a harness at all.</summary>
public sealed class DetectRules
{
    /// <summary>Regexes on the launch command line (dedicated agent profiles).</summary>
    public string[] Commandline { get; init; } = [];

    /// <summary>Regexes on the terminal title.</summary>
    public string[] Title { get; init; } = [];

    /// <summary>Process image names without extension, for the foreground-process probe.</summary>
    public string[] Process { get; init; } = [];
}

/// <summary>
/// Everything OverShell knows about one harness, from a JSONC file. Bundled defaults ship
/// in the assembly; a user file with the same <see cref="Id"/> under
/// <c>%APPDATA%\OverShell\agents\</c> replaces it wholesale — herdr's override model, so a
/// user never has to reason about merges when a new agent version changes its prompts.
/// </summary>
/// <summary>What to type for yes and no at a harness's permission prompt.</summary>
public sealed class AnswerKeys
{
    public string? Approve { get; init; }

    public string? Deny { get; init; }
}

public sealed class AgentRuleSet
{
    public required string Id { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>One character for compact UI. Chosen from fonts a WPF chrome has: Segoe UI Symbol / emoji.</summary>
    public string Glyph { get; init; } = "◆";

    /// <summary>
    /// Optional vector icon as path-markup data on a 16x16 grid (<c>M8,0 L16,8 L8,16 L0,8 Z</c>).
    /// The bundled harnesses have theirs in the application's theme; a user rule set may bring
    /// its own here. Null means: the theme's icon for this id, else the generic one, else <see cref="Glyph"/>.
    /// </summary>
    public string? Icon { get; init; }

    public DetectRules Detect { get; init; } = new();

    /// <summary>Rules on the OSC 0/2 title — Claude Code's spinner glyphs, the OpenCode TUI plugin's icons.</summary>
    public StateRules Title { get; init; } = new();

    /// <summary>Rules on the last <see cref="ScreenRows"/> rows of the viewport, joined with newlines.</summary>
    public StateRules Screen { get; init; } = new();

    /// <summary>Rules on OSC 9 / 99 / 777 notification bodies.</summary>
    public StateRules Notification { get; init; } = new();

    /// <summary>OSC 9;4 progress state (0–4) → state. Absent keys only update the progress display.</summary>
    public Dictionary<string, AgentState> Progress { get; init; } = new(StringComparer.Ordinal)
    {
        ["1"] = AgentState.Working,
        ["2"] = AgentState.Error,
        ["3"] = AgentState.Working,
    };

    /// <summary>Regex with one capture group that extracts a one-line summary from the bottom rows.</summary>
    public string? Summary { get; init; }

    /// <summary>Quiet time after output before an agent with no explicit idle signal is considered idle.</summary>
    public int IdleAfterMs { get; init; } = 1500;

    /// <summary>How many bottom rows the screen rules see.</summary>
    public int ScreenRows { get; init; } = 12;

    /// <summary>Command that resumes a session by id, with <c>{sessionId}</c> — filled by integrations.</summary>
    public string? ResumeCommand { get; init; }

    /// <summary>
    /// Command that resumes the harness's most recent session when no id is known
    /// (<c>opencode --continue</c>). Used at restore only when <c>session.resumeWithoutId</c>
    /// allows it: "most recent" is the tool's notion, usually per directory, so two tabs of
    /// one harness in one directory may both land on the same session.
    /// </summary>
    public string? ResumeLastCommand { get; init; }

    /// <summary>
    /// Keystrokes that answer the harness's permission prompt when no exact channel exists
    /// (§12.17): <c>approve</c> and <c>deny</c>, sent as typed (<c>\r</c> for Enter). Null means
    /// the harness has no one-key answers; a reply is then free text or the integration's.
    /// </summary>
    public AnswerKeys? Answers { get; init; }

    /// <summary>The command that starts this harness in a shell (<c>opencode</c>), for spawning a new agent tab (§12.18).</summary>
    public string? Launch { get; init; }

    /// <summary>
    /// Whether these rules can tell idle apart from silence: a title, screen or progress rule
    /// that names idle (progress state 0 does not count - it clears the bar, it never sets a
    /// state). When they can, a first prompt waits for that evidence (or the integration's
    /// word); when they cannot, quiet output is the best there is.
    /// </summary>
    [JsonIgnore]
    public bool KnowsIdle => Title.Idle.Length > 0 || Screen.Idle.Length > 0 || Progress.Any(p => p.Key != "0" && p.Value == AgentState.Idle);

    [JsonIgnore]
    internal Regex? SummaryRegex => _summary ??= Compile(Summary);

    [JsonIgnore]
    internal Regex[] CommandlineRegexes => _commandline ??= Compile(Detect.Commandline);

    [JsonIgnore]
    internal Regex[] TitleRegexes => _title ??= Compile(Detect.Title);

    private Regex? _summary;
    private Regex[]? _commandline;
    private Regex[]? _title;

    private static Regex[] Compile(string[] patterns) =>
        patterns.Select(Compile).Where(r => r is not null).ToArray()!;

    private static Regex? Compile(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
