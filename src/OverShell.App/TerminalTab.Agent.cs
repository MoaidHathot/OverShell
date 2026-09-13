using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OverShell.App.Agents;
using OverShell.App.Terminal;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;

namespace OverShell.App;

/// <summary>
/// The agent side of a tab: which harness is running, what it is doing, and whether the
/// user has seen the result. All decisions are made by <see cref="AgentStateMachine"/> on
/// the UI thread; this file only gathers the evidence — stream signals from the I/O
/// thread, screen snapshots and process probes from worker threads, reports from the
/// integration endpoint — and hands it over in order.
/// </summary>
public sealed partial class TerminalTab
{
    private readonly ConcurrentQueue<TerminalSignal> _signals = new();
    private readonly Dictionary<string, string> _projectCache = new(StringComparer.OrdinalIgnoreCase);

    private AgentServices _agents = null!;
    private int _drainScheduled;
    private int _outputPending;
    private long _lastOutputTicks;
    private long _snapshotVersion = -1;
    private long _probeVersion = -1;
    private DateTimeOffset _lastSnapshotAt;
    private DateTimeOffset _lastProbeAt;
    private bool _snapshotBusy;
    private bool _probeBusy;
    private nint _hwnd;
    private bool _viewed;

    private string? _harnessFromCommandline;
    private string? _harnessFromProcess;
    private string? _harnessFromTitle;
    private string? _harnessFromIntegration;
    private bool? _userOverride;
    private string? _userLabel;
    private string? _group;
    private string? _pendingResume;
    private DateTimeOffset _resumeScheduledAt;
    private readonly List<AgentTransition> _history = [];
    private int _integrationMissingProbes;
    private IReadOnlyList<string> _lastImages = [];

    /// <summary>Short stable id for this run: what integrations address and what the endpoint lists.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public AgentStateMachine Agent { get; private set; } = null!;

    /// <summary>Rule-set id of the harness detected in this tab, or null for a plain shell.</summary>
    public string? Harness { get; private set; }

    public bool IsAgent => Agent.IsAgent;

    public AgentState State => Agent.State;

    public bool NeedsAttention => Agent.NeedsAttention;

    public bool Unread => Agent.Unread;

    /// <summary>The name the user gave this tab, if any. Persisted by the window against profile + directory.</summary>
    public string? UserLabel
    {
        get => _userLabel;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (next == _userLabel)
            {
                return;
            }

            _userLabel = next;
            Raise();
            Raise(nameof(Label));
        }
    }

    /// <summary>First line of the tab item: the user's label, else the harness name, else the shell title.</summary>
    public string Label => _userLabel ?? (IsAgent ? Agent.Rules.DisplayName : Title);

    /// <summary>An explicit group the user put this tab in; null means "by project" wherever grouping applies.</summary>
    public string? Group
    {
        get => _group;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (next == _group)
            {
                return;
            }

            _group = next;
            Raise();
        }
    }

    /// <summary>The command that resumes this tab's agent session, filled in from the last report; null when unknown.</summary>
    public string? ResumeCommand { get; private set; }

    /// <summary>The last state changes, oldest first, for the explain panel.</summary>
    public IReadOnlyList<AgentTransition> History => _history;

    /// <summary>Image names below the shell as of the last process probe, nearest first.</summary>
    public IReadOnlyList<string> LastProcessImages => _lastImages;

    /// <summary>
    /// Types <paramref name="command"/> into the shell once it has shown its first prompt
    /// and gone quiet — a restored agent tab picking up where it left off. One shot.
    /// </summary>
    internal void ScheduleResume(string command)
    {
        _pendingResume = command;
        _resumeScheduledAt = DateTimeOffset.Now;
    }

    /// <summary>Runs the known resume command now, in this tab. False when there is none.</summary>
    internal bool Resume()
    {
        if (string.IsNullOrWhiteSpace(ResumeCommand) || _disposed)
        {
            return false;
        }

        SendText(ResumeCommand + "\r");
        _agents.Trace.Write($"[{Id}] resume: {ResumeCommand}");
        return true;
    }

    /// <summary>Second line: state and what the agent says it is doing, or the project.</summary>
    public string Detail
    {
        get
        {
            var state = StateText;
            var what = IsAgent ? Agent.Summary ?? Project : Project;
            return state.Length == 0 ? what : what.Length == 0 ? state : $"{state} · {what}";
        }
    }

    /// <summary>The repository this tab works in (directory holding <c>.git</c>), else the directory's own name.</summary>
    public string Project
    {
        get
        {
            var cwd = _workingDirectory;
            if (string.IsNullOrWhiteSpace(cwd))
            {
                return string.Empty;
            }

            if (_projectCache.TryGetValue(cwd, out var cached))
            {
                return cached;
            }

            var project = FindProjectName(cwd);
            _projectCache[cwd] = project;
            return project;
        }
    }

    public string StateText => State switch
    {
        AgentState.Idle => "idle",
        AgentState.Working => "working",
        AgentState.Blocked => "needs you",
        AgentState.Done => "done",
        AgentState.Error => "error",
        AgentState.Exited => "exited",
        _ => string.Empty,
    };

    /// <summary>One character for the harness, from its rule file; empty for shells.</summary>
    public string Glyph => IsAgent ? Agent.Rules.Glyph : string.Empty;

    /// <summary>The state's colour from the theme, for dots and badges.</summary>
    public Brush StateBrush => (Brush)Application.Current.FindResource(State switch
    {
        AgentState.Working => "State.Working",
        AgentState.Blocked => "State.Blocked",
        AgentState.Done => "State.Done",
        AgentState.Error => "State.Error",
        AgentState.Exited => "State.Exited",
        AgentState.Idle => "State.Idle",
        _ => "Text.Disabled",
    });

    /// <summary>Everything the hover should say: the shell's title, the directory, and why the state is what it is.</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string>(4) { Title };
            if (!string.IsNullOrEmpty(_workingDirectory))
            {
                lines.Add(_workingDirectory);
            }

            if (IsAgent)
            {
                lines.Add($"{Agent.Rules.DisplayName} · {StateText} — {Agent.Explain}");
            }
            else if (State != AgentState.Unknown)
            {
                lines.Add($"{StateText} — {Agent.Explain}");
            }

            return string.Join('\n', lines);
        }
    }

    /// <summary>The user says an agent runs here even though nothing recognised it. Cleared when a harness is detected.</summary>
    internal void MarkAsAgent()
    {
        _userOverride = true;
        ReevaluateHarness(DateTimeOffset.Now, "marked as agent by user");
    }

    /// <summary>The user says this is a plain shell; detection stays quiet until a different harness shows up.</summary>
    internal void MarkAsShell()
    {
        _userOverride = false;
        ReevaluateHarness(DateTimeOffset.Now, "marked as shell by user");
    }

    /// <summary>The rule files were reloaded: the same harness id now means the new rules; re-detect from what is known.</summary>
    internal void RulesReloaded()
    {
        var now = DateTimeOffset.Now;
        _harnessFromCommandline = _agents.Rules.DetectFromCommandline(Session.Descriptor.CommandLine);
        _harnessFromTitle = _agents.Rules.DetectFromTitle(_shellTitle);
        ReevaluateHarness(now, "rules reloaded", force: true);
        RaiseAgentProperties();
    }

    public double? Progress => Agent.Progress;

    public bool ProgressIndeterminate => Agent.ProgressIndeterminate;

    public string? Summary => Agent.Summary;

    /// <summary>The viewport as last read through UI Automation, top to bottom; empty until the first snapshot.</summary>
    public IReadOnlyList<string> ScreenRows { get; private set; } = [];

    /// <summary>The last rows of <see cref="ScreenRows"/> joined, for cards and the switcher preview.</summary>
    public string ScreenText { get; private set; } = string.Empty;

    /// <summary>
    /// Set while a dashboard or switcher wants this tab's screen: snapshots then run for
    /// shells too, about once a second while output changes, regardless of settling.
    /// </summary>
    public bool ScreenWatched { get; set; }

    /// <summary>"just now", "12 s ago", "3 min ago" — refreshed by the heartbeat.</summary>
    public string ActivityText { get; private set; } = string.Empty;

    /// <summary>Takes a snapshot as soon as possible, for a preview that cannot wait for the heartbeat.</summary>
    internal void RequestScreen()
    {
        if (!_disposed && !_snapshotBusy)
        {
            _ = SnapshotAsync(OutputVersion, DateTimeOffset.Now);
        }
    }

    /// <summary>Raised on the UI thread when the agent wants the user; the notification pipeline listens.</summary>
    public event Action<TerminalTab, AgentAttention>? AttentionRequested;

    /// <summary>Raised on the UI thread after every state transition.</summary>
    public event Action<TerminalTab, AgentTransition>? StateChanged;

    public TabSummary Summarize() => new(Id, Label, Harness, State.ToString(), Project, Agent.Explain);

    // ------------------------------------------------------------- lifecycle

    private void InitializeAgent(AgentServices agents, string commandLine)
    {
        _agents = agents;
        _harnessFromCommandline = agents.Rules.DetectFromCommandline(commandLine);

        var rules = agents.Rules.Find(_harnessFromCommandline) ?? agents.Rules.Generic;
        var isAgent = _harnessFromCommandline is not null || agents.Detection.TreatUnknownAsAgent;
        Harness = _harnessFromCommandline;

        Agent = new AgentStateMachine(rules, isAgent);
        Agent.Transitioned += OnTransition;
        Agent.AttentionRequested += a => AttentionRequested?.Invoke(this, a);

        _stream.Signal += OnSignal;

        agents.Trace.Write($"[{Id}] tab created profile='{Profile.Name}' commandline='{commandLine}' harness={Harness ?? "-"} agent={isAgent}");
    }

    /// <summary>Whether the user can see this tab right now: it is the active tab and the window is in the foreground.</summary>
    internal void SetViewed(bool viewed)
    {
        if (_viewed == viewed)
        {
            return;
        }

        _viewed = viewed;
        Agent.SetViewed(viewed, DateTimeOffset.Now);
        RaiseAgentProperties();
    }

    /// <summary>
    /// The window's 500 ms heartbeat: lets the quiet timer run, takes a screen snapshot when
    /// output has settled, and asks the process tree what is running when output changed.
    /// </summary>
    internal void Heartbeat(DateTimeOffset now)
    {
        if (_disposed)
        {
            return;
        }

        var before = Agent.State;
        Agent.Tick(now);
        if (Agent.State != before)
        {
            RaiseAgentProperties();
        }

        var activity = Agent.LastActivity is { } last ? Ago(now - last) : string.Empty;
        if (activity != ActivityText)
        {
            ActivityText = activity;
            Raise(nameof(ActivityText));
        }

        var version = OutputVersion;

        if (_pendingResume is { } resume)
        {
            // The shell has printed its prompt (first output happened) and has been quiet for a
            // second: typing now lands on the prompt, not into a banner mid-print. If the shell
            // never says anything, give up rather than type into the void.
            var lastOutputTicks = Volatile.Read(ref _lastOutputTicks);
            var quiet = lastOutputTicks != 0 && now - new DateTimeOffset(lastOutputTicks, TimeSpan.Zero) >= TimeSpan.FromSeconds(1);
            var waited = now - _resumeScheduledAt;
            if (HasStarted && IsRunning && quiet && waited >= TimeSpan.FromSeconds(1.5))
            {
                _pendingResume = null;
                SendText(resume + "\r");
                _agents.Trace.Write($"[{Id}] resumed after {waited.TotalSeconds:F1}s: {resume}");
            }
            else if (waited > TimeSpan.FromSeconds(20) || (HasStarted && !IsRunning))
            {
                _pendingResume = null;
                _agents.Trace.Write($"[{Id}] resume abandoned: shell not ready in time");
            }
        }

        if (!_snapshotBusy && version != _snapshotVersion)
        {
            var sinceOutput = now - new DateTimeOffset(Volatile.Read(ref _lastOutputTicks), TimeSpan.Zero);
            var sinceSnapshot = now - _lastSnapshotAt;

            // Agents: wait for output to settle, so a prompt is read whole. Watched screens
            // (cards, previews): a steady once-a-second refresh while output flows.
            var agentDue = IsAgent &&
                           sinceOutput >= TimeSpan.FromMilliseconds(_agents.Detection.SnapshotDebounceMs) &&
                           sinceSnapshot >= TimeSpan.FromMilliseconds(_agents.Detection.SnapshotMinIntervalMs);
            var watchedDue = ScreenWatched && sinceSnapshot >= TimeSpan.FromSeconds(1);

            if (agentDue || watchedDue)
            {
                _ = SnapshotAsync(version, now);
            }
        }

        // Probe when output changed — a new program is the usual reason — and periodically
        // while an integration is authoritative, because the release rule needs to notice
        // a harness process that has gone without any further output.
        var probeDue = version != _probeVersion || _integrationMissingProbes > 0 || Agent.Authority == AgentAuthority.Integration;
        if (!_probeBusy && _harnessFromCommandline is null && probeDue && IsRunning &&
            now - _lastProbeAt >= TimeSpan.FromMilliseconds(_agents.Detection.ProcessProbeIntervalMs) &&
            Session.ProcessId is { } pid)
        {
            _ = ProbeProcessesAsync(pid, version, now);
        }
    }

    /// <summary>A hook or plugin spoke about this tab. Called on the UI thread.</summary>
    internal void ApplyReport(IntegrationReport report)
    {
        var now = DateTimeOffset.Now;

        if (report.Release)
        {
            Agent.OnRelease(report.Source, now);
            if (report.State == AgentState.Exited && _harnessFromIntegration is not null)
            {
                // The harness said goodbye: the tab is a shell again once its process is gone; the
                // probe confirms.
                _harnessFromIntegration = null;
                ReevaluateHarness(now, $"released by {report.Source}");
            }
        }
        else
        {
            if (report.Harness is { } harness && harness != _harnessFromIntegration)
            {
                _harnessFromIntegration = harness;
                ReevaluateHarness(now, $"reported by {report.Source}");
            }

            Agent.OnReport(report.Source, report.Seq, report.State, report.Message, report.Summary, report.SessionId, now);

            // Whatever resumes this session: the integration's own command, else the rule file's
            // pattern with the id filled in. Kept for restore and for `tab.resume`.
            var sessionId = report.SessionId ?? Agent.SessionId;
            var resume = report.ResumeCommand
                ?? (sessionId is not null && Agent.Rules.ResumeCommand is { } pattern ? pattern.Replace("{sessionId}", sessionId, StringComparison.Ordinal) : null);
            if (resume is not null && resume != ResumeCommand)
            {
                ResumeCommand = resume;
                Raise(nameof(ResumeCommand));
            }
        }

        _agents.Trace.Write($"[{Id}] report source={report.Source} seq={report.Seq} state={report.State} -> {Agent.State} ({Agent.Explain})");
        RaiseAgentProperties();
    }

    // -------------------------------------------------------------- evidence

    /// <summary>I/O thread: a chunk arrived. Coalesced into one UI-thread drain per burst.</summary>
    private void NoteOutput()
    {
        Volatile.Write(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _outputPending, 1);
        ScheduleDrain();
    }

    /// <summary>I/O thread: a bell, notification, progress or prompt mark.</summary>
    private void OnSignal(TerminalSignal signal)
    {
        _signals.Enqueue(signal);
        ScheduleDrain();
    }

    private void ScheduleDrain()
    {
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
        {
            _dispatcher.BeginInvoke(Drain, DispatcherPriority.Background);
        }
    }

    private void Drain()
    {
        Volatile.Write(ref _drainScheduled, 0);
        if (_disposed)
        {
            return;
        }

        var before = (Agent.State, Agent.Progress, Agent.ProgressIndeterminate, Agent.Summary);
        if (Interlocked.Exchange(ref _outputPending, 0) == 1)
        {
            Agent.OnOutput(new DateTimeOffset(Volatile.Read(ref _lastOutputTicks), TimeSpan.Zero).ToLocalTime());
        }

        while (_signals.TryDequeue(out var signal))
        {
            switch (signal.Kind)
            {
                case TerminalSignalKind.Bell:
                    Agent.OnBell(signal.At);
                    break;
                case TerminalSignalKind.Notification:
                    Agent.OnNotification(signal.Title, signal.Body ?? string.Empty, signal.At);
                    break;
                case TerminalSignalKind.Progress:
                    Agent.OnProgress(signal.State, signal.Percent, signal.At);
                    break;
                case TerminalSignalKind.PromptMark:
                    Agent.OnPromptMark(signal.Mark, signal.At);
                    break;
            }

            _agents.Trace.Write($"[{Id}] signal {signal.Kind}{(signal.Kind == TerminalSignalKind.Progress ? $" state={signal.State} pct={signal.Percent}" : signal.Kind == TerminalSignalKind.PromptMark ? $" mark={signal.Mark}" : string.Empty)} -> {Agent.State}");
        }

        if (before != (Agent.State, Agent.Progress, Agent.ProgressIndeterminate, Agent.Summary))
        {
            RaiseAgentProperties();
        }
    }

    /// <summary>UI thread: the OSC title changed. Both a harness clue and a state clue.</summary>
    private void OnTitleChanged(string title)
    {
        var now = DateTimeOffset.Now;
        var fromTitle = _agents.Rules.DetectFromTitle(title);
        if (fromTitle != _harnessFromTitle)
        {
            _harnessFromTitle = fromTitle;
            ReevaluateHarness(now, $"title '{title}'");
        }

        Agent.OnTitle(title, now);
        RaiseAgentProperties();
    }

    private async Task SnapshotAsync(long version, DateTimeOffset now)
    {
        _snapshotBusy = true;
        _snapshotVersion = version;
        _lastSnapshotAt = now;
        try
        {
            if (_hwnd == 0)
            {
                _hwnd = MainWindow.FindTerminalHwnd(View);
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var rows = await _agents.Screen.ReadRowsAsync(_hwnd);
            if (_disposed || rows is null)
            {
                return;
            }

            ScreenRows = rows;
            ScreenText = string.Join('\n', rows.Skip(Math.Max(0, rows.Count - 14)));
            Raise(nameof(ScreenRows));
            Raise(nameof(ScreenText));

            if (!IsAgent)
            {
                return;
            }

            var before = Agent.State;
            Agent.OnScreen(rows, DateTimeOffset.Now);
            _agents.Trace.Write($"[{Id}] snapshot {rows.Count} rows in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms -> {Agent.State}{(Agent.State != before ? $" ({Agent.Explain})" : string.Empty)}");
            RaiseAgentProperties();
        }
        finally
        {
            _snapshotBusy = false;
        }
    }

    private async Task ProbeProcessesAsync(int pid, long version, DateTimeOffset now)
    {
        _probeBusy = true;
        _probeVersion = version;
        _lastProbeAt = now;
        try
        {
            var images = await Task.Run(() => ProcessTree.Descendants(pid));
            if (_disposed)
            {
                return;
            }

            string? found = null;
            foreach (var image in images)
            {
                if (_agents.Rules.DetectFromProcess(image) is { } id)
                {
                    found = id;
                    break;
                }
            }

            if (!images.SequenceEqual(_lastImages, StringComparer.OrdinalIgnoreCase))
            {
                _lastImages = images;
                _agents.Trace.Write($"[{Id}] processes below shell: [{string.Join(", ", images)}] -> {found ?? "-"}");
            }

            if (found != _harnessFromProcess)
            {
                _harnessFromProcess = found;
                ReevaluateHarness(DateTimeOffset.Now, $"processes [{string.Join(", ", images)}]");
            }

            ReleaseIntegrationIfGone(images);
        }
        finally
        {
            _probeBusy = false;
        }
    }

    /// <summary>
    /// An integration has no exit event we can rely on (<c>opencode run</c> just ends; a TUI
    /// is quit), so its word stands only while a process of its harness still runs below
    /// the shell. Two consecutive probes without one — five seconds — hand the tab back to
    /// the detector; an unseen Done survives that hand-over.
    /// </summary>
    private void ReleaseIntegrationIfGone(IReadOnlyList<string> images)
    {
        if (_harnessFromIntegration is null || Agent.Authority != AgentAuthority.Integration ||
            _agents.Rules.Find(_harnessFromIntegration) is not { Detect.Process.Length: > 0 } rules)
        {
            _integrationMissingProbes = 0;
            return;
        }

        var present = images.Any(image => rules.Detect.Process.Any(p => string.Equals(p, image, StringComparison.OrdinalIgnoreCase)));
        if (present)
        {
            _integrationMissingProbes = 0;
            return;
        }

        if (++_integrationMissingProbes < 2)
        {
            return;
        }

        _integrationMissingProbes = 0;
        var source = Agent.AuthoritySource!;
        var now = DateTimeOffset.Now;
        Agent.OnRelease(source, now);
        _harnessFromIntegration = null;
        _agents.Trace.Write($"[{Id}] released {source}: no {rules.Id} process below the shell");
        ReevaluateHarness(now, $"{rules.Id} process gone");
        RaiseAgentProperties();
    }

    /// <summary>
    /// The harness in force is the most trustworthy clue available: an integration's own
    /// word, then the launch command line, then a known process below the shell, then the
    /// title. Switching rules clears agent evidence when the tab becomes a shell again.
    /// </summary>
    private void ReevaluateHarness(DateTimeOffset now, string trigger, bool force = false)
    {
        var id = _harnessFromIntegration ?? _harnessFromCommandline ?? _harnessFromProcess ?? _harnessFromTitle;

        // A detected harness outranks the user's earlier "this is a shell"; the override is
        // for tabs detection cannot read, not a way to silence a recognised agent.
        if (id is not null && id != Harness)
        {
            _userOverride = null;
        }

        var isAgent = _userOverride ?? (id is not null || _agents.Detection.TreatUnknownAsAgent);

        if (!force && id == Harness && isAgent == IsAgent)
        {
            return;
        }

        Harness = id;
        var rules = _agents.Rules.Find(id) ?? _agents.Rules.Generic;
        Agent.SetRules(rules, isAgent, now);

        if (isAgent && _shellTitle is { } title)
        {
            // Title rules never ran while this was a shell; the current title is evidence now.
            Agent.OnTitle(title, now);
        }

        _agents.Trace.Write($"[{Id}] harness={id ?? "-"} agent={isAgent} rules={rules.Id} via {trigger}");
        Raise(nameof(Harness));
        Raise(nameof(IsAgent));
        Raise(nameof(Glyph));
        RaiseAgentProperties();
    }

    private void OnTransition(AgentTransition transition)
    {
        _agents.Trace.Write($"[{Id}] {transition.From} -> {transition.To}: {transition.Reason}");
        if (_history.Count >= 40)
        {
            _history.RemoveAt(0);
        }

        _history.Add(transition);
        StateChanged?.Invoke(this, transition);
    }

    private void RaiseAgentProperties()
    {
        Raise(nameof(State));
        Raise(nameof(StateText));
        Raise(nameof(StateBrush));
        Raise(nameof(NeedsAttention));
        Raise(nameof(Unread));
        Raise(nameof(Label));
        Raise(nameof(Detail));
        Raise(nameof(SidebarDetail));
        Raise(nameof(Tooltip));
        Raise(nameof(Progress));
        Raise(nameof(ProgressIndeterminate));
        Raise(nameof(Summary));
    }

    private static string FindProjectName(string cwd)
    {
        try
        {
            if (Core.Git.GitRepository.FindRoot(cwd) is { } root)
            {
                return new DirectoryInfo(root).Name;
            }

            var dir = new DirectoryInfo(cwd);
            return dir.Name.Length > 0 ? dir.Name : cwd;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return cwd;
        }
    }

    private static string Ago(TimeSpan age)
    {
        if (age < TimeSpan.FromSeconds(3))
        {
            return "just now";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return $"{(int)age.TotalSeconds} s ago";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        return age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} h ago" : $"{(int)age.TotalDays} d ago";
    }
}
