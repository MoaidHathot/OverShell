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
public sealed class AgentRuleSet
{
    public required string Id { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>One character for compact UI. Chosen from fonts a WPF chrome has: Segoe UI Symbol / emoji.</summary>
    public string Glyph { get; init; } = "◆";

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
