namespace OverShell.Core.Agents;

/// <summary>A state change, with the evidence that caused it.</summary>
public sealed record AgentTransition(AgentState From, AgentState To, string Reason, DateTimeOffset At);

/// <summary>Something the user should hear about, with the agent's own words when it had any.</summary>
public sealed record AgentAttention(AgentState State, string Reason, string? Message, DateTimeOffset At);

/// <summary>
/// Decides what one tab's agent is doing from whatever evidence arrives, in the order it
/// arrives. Pure: every input carries its own timestamp, so the same sequence always gives
/// the same answer — and a test can replay a recorded session.
/// <para>
/// Two authorities, never both (§12.1): while an integration is reporting for this tab its
/// word is final; otherwise the detector weighs titles, screen text, progress sequences,
/// notifications and output activity. <b>Blocked is strict</b>: it comes only from an
/// explicit match or an integration, never from silence, because a false "needs you" is
/// a false alarm every time.
/// </para>
/// </summary>
public sealed class AgentStateMachine
{
    /// <summary>Output must keep coming for this long before activity alone means "working".</summary>
    public static readonly TimeSpan ActivityWorkingAfter = TimeSpan.FromMilliseconds(400);

    /// <summary>A heuristic Working→Idle counts as "done" only after this much work; a banner is not a turn.</summary>
    public static readonly TimeSpan MinimumWorkForDone = TimeSpan.FromSeconds(3);

    /// <summary>Shell commands shorter than this do not deserve a "done" mark.</summary>
    public static readonly TimeSpan MinimumCommandForDone = TimeSpan.FromSeconds(5);

    /// <summary>Repeated bells inside this window are one bell.</summary>
    public static readonly TimeSpan BellDebounce = TimeSpan.FromSeconds(2);

    private AgentRuleSet _rules;
    private bool _isAgent;
    private bool _viewed = true;

    // Detector evidence, each a *state* that stays until replaced.
    private (AgentState State, string Why)? _fromTitle;
    private (AgentState State, string Why)? _fromScreen;
    private (AgentState State, string Why)? _fromProgress;
    private DateTimeOffset? _firstOutputInBurst;
    private DateTimeOffset? _lastOutput;
    private DateTimeOffset? _workingSince;
    private DateTimeOffset? _lastBell;
    private DateTimeOffset? _commandStarted;

    // Integration evidence.
    private string? _authoritySource;
    private long _authoritySeq = long.MinValue;
    private DateTimeOffset _authorityAt;

    public AgentStateMachine(AgentRuleSet rules, bool isAgent)
    {
        _rules = rules;
        _isAgent = isAgent;
    }

    public AgentState State { get; private set; } = AgentState.Unknown;

    public AgentAuthority Authority => _authoritySource is null ? AgentAuthority.Detector : AgentAuthority.Integration;

    public string? AuthoritySource => _authoritySource;

    /// <summary>The evidence behind the current state — herdr's <c>agent explain</c>.</summary>
    public string Explain { get; private set; } = "no signal yet";

    /// <summary>True while a Done/Blocked/Error has not been looked at.</summary>
    public bool Unread { get; private set; }

    public DateTimeOffset? AttentionSince { get; private set; }

    public bool NeedsAttention => State is AgentState.Blocked or AgentState.Error || (State == AgentState.Done && Unread);

    /// <summary>0–1 when a determinate progress is known.</summary>
    public double? Progress { get; private set; }

    public bool ProgressIndeterminate { get; private set; }

    /// <summary>One line describing what the agent is doing, from the integration or the screen.</summary>
    public string? Summary { get; private set; }

    /// <summary>Session identity an integration reported, for resume.</summary>
    public string? SessionId { get; private set; }

    public DateTimeOffset? LastActivity { get; private set; }

    public bool IsAgent => _isAgent;

    public AgentRuleSet Rules => _rules;

    public event Action<AgentTransition>? Transitioned;

    /// <summary>Raised once per attention-worthy event, with the message the agent gave if any.</summary>
    public event Action<AgentAttention>? AttentionRequested;

    // ------------------------------------------------------------------ configuration

    /// <summary>Switches harness rules (e.g. the title revealed OpenCode). Evidence is re-read against the new rules lazily.</summary>
    public void SetRules(AgentRuleSet rules, bool isAgent, DateTimeOffset now)
    {
        var becameAgent = isAgent && !_isAgent;
        _rules = rules;
        _isAgent = isAgent;

        if (!isAgent)
        {
            // Back to a plain shell: the agent's evidence no longer applies — except an
            // unseen Done, which is the whole point of the mark; viewing clears it.
            _fromTitle = _fromScreen = _fromProgress = null;
            Progress = null;
            ProgressIndeterminate = false;
            Summary = null;
            _authoritySource = null;
            SessionId = null;
            if (State is not (AgentState.Exited or AgentState.Done))
            {
                Set(AgentState.Unknown, "shell", now);
            }
        }
        else if (becameAgent)
        {
            Evaluate(now, "harness detected");
        }
    }

    /// <summary>Whether the tab is on screen and active. Done clears on viewing; nothing else changes.</summary>
    public void SetViewed(bool viewed, DateTimeOffset now)
    {
        _viewed = viewed;
        if (viewed)
        {
            Unread = false;
            if (State == AgentState.Done)
            {
                // A shell has no "idle"; its resting state is Unknown.
                Set(_isAgent ? AgentState.Idle : AgentState.Unknown, "viewed", now);
            }
            else if (State is not (AgentState.Blocked or AgentState.Error))
            {
                AttentionSince = null;
            }
        }
    }

    // ------------------------------------------------------------------ detector inputs

    public void OnTitle(string title, DateTimeOffset now)
    {
        if (!_isAgent)
        {
            return;
        }

        var match = _rules.Title.Compiled.Match(title);
        var next = match is { } m ? (m.State, $"title matched /{m.Pattern}/") : ((AgentState, string)?)null;
        if (next?.Item1 != _fromTitle?.State || next?.Item2 != _fromTitle?.Why)
        {
            _fromTitle = next;
            Evaluate(now, "title");
        }
    }

    /// <summary>OSC 9;4 progress: state 0 clear, 1 normal, 2 error, 3 indeterminate, 4 paused/warning.</summary>
    public void OnProgress(int state, int percent, DateTimeOffset now)
    {
        if (!_isAgent && state == 0)
        {
            Progress = null;
            ProgressIndeterminate = false;
            return;
        }

        switch (state)
        {
            case 0:
                Progress = null;
                ProgressIndeterminate = false;
                break;
            case 3:
                Progress = null;
                ProgressIndeterminate = true;
                break;
            default:
                Progress = Math.Clamp(percent, 0, 100) / 100.0;
                ProgressIndeterminate = false;
                break;
        }

        if (!_isAgent)
        {
            return;
        }

        // State 0 clears; a state the rules do not map keeps the previous opinion.
        var mapped = _rules.Progress.TryGetValue(state.ToString(), out var s) ? s : (AgentState?)null;
        _fromProgress = state == 0 ? null : mapped is { } ms ? (ms, $"progress state {state}") : _fromProgress;

        Evaluate(now, "progress");
    }

    public void OnBell(DateTimeOffset now)
    {
        LastActivity = now;
        if (_lastBell is { } last && now - last < BellDebounce)
        {
            return;
        }

        _lastBell = now;

        if (!_isAgent)
        {
            // A shell rang: something finished. Worth a mark when nobody is looking.
            if (!_viewed && State != AgentState.Exited)
            {
                Set(AgentState.Done, "bell", now);
                Unread = true;
                AttentionSince ??= now;
                AttentionRequested?.Invoke(new AgentAttention(AgentState.Done, "bell", null, now));
            }

            return;
        }

        if (Authority == AgentAuthority.Integration)
        {
            return;
        }

        if (!_viewed && State is not (AgentState.Blocked or AgentState.Error or AgentState.Exited))
        {
            Set(AgentState.Done, "bell", now);
            Unread = true;
            AttentionSince ??= now;
            AttentionRequested?.Invoke(new AgentAttention(AgentState.Done, "bell", null, now));
        }
    }

    /// <summary>OSC 9 (body), OSC 99 (kitty title/body), OSC 777 (title;body).</summary>
    public void OnNotification(string? title, string body, DateTimeOffset now)
    {
        LastActivity = now;
        if (!_isAgent || Authority == AgentAuthority.Integration || State == AgentState.Exited)
        {
            return;
        }

        var text = string.IsNullOrEmpty(title) ? body : title + "\n" + body;
        var match = _rules.Notification.Compiled.Match(text);
        var state = match?.State ?? AgentState.Done;
        var reason = match is { } m ? $"notification matched /{m.Pattern}/" : "notification";

        if (state == AgentState.Blocked || state == AgentState.Error)
        {
            Set(state, reason, now);
            Unread = true;
            AttentionSince ??= now;
            AttentionRequested?.Invoke(new AgentAttention(state, reason, body, now));
        }
        else if (!_viewed)
        {
            Set(AgentState.Done, reason, now);
            Unread = true;
            AttentionSince ??= now;
            AttentionRequested?.Invoke(new AgentAttention(AgentState.Done, reason, body, now));
        }
    }

    /// <summary>OSC 133 shell-integration marks: A prompt start, B prompt end, C command start, D command end.</summary>
    public void OnPromptMark(char mark, DateTimeOffset now)
    {
        LastActivity = now;
        switch (mark)
        {
            case 'C':
                _commandStarted = now;
                break;

            case 'D':
                if (!_isAgent && _commandStarted is { } started && !_viewed && now - started >= MinimumCommandForDone && State != AgentState.Exited)
                {
                    Set(AgentState.Done, "command finished", now);
                    Unread = true;
                    AttentionSince ??= now;
                    AttentionRequested?.Invoke(new AgentAttention(AgentState.Done, "command finished", null, now));
                }

                _commandStarted = null;
                break;

            case 'A':
                // A fresh shell prompt: whatever agent was here has returned to the shell.
                _commandStarted = null;
                break;
        }
    }

    public void OnOutput(DateTimeOffset now)
    {
        LastActivity = now;
        if (!_isAgent || Authority == AgentAuthority.Integration)
        {
            return;
        }

        if (_lastOutput is null || now - _lastOutput.Value > TimeSpan.FromMilliseconds(Math.Max(_rules.IdleAfterMs, 500)))
        {
            _firstOutputInBurst = now;
        }

        _lastOutput = now;
        Evaluate(now, "output");
    }

    /// <summary>The last rows of the viewport, oldest first, as the terminal reports them.</summary>
    public void OnScreen(IReadOnlyList<string> bottomRows, DateTimeOffset now)
    {
        if (!_isAgent)
        {
            return;
        }

        var rows = bottomRows.Count > _rules.ScreenRows ? bottomRows.Skip(bottomRows.Count - _rules.ScreenRows).ToList() : bottomRows;
        var joined = string.Join('\n', rows.Select(r => r.TrimEnd()));

        // The screen's summary tracks the screen: when the status line goes, so does the summary.
        if (Authority == AgentAuthority.Detector && _rules.SummaryRegex is not null)
        {
            Summary = ExtractSummary(rows);
        }

        if (Authority == AgentAuthority.Integration)
        {
            return;
        }

        var match = _rules.Screen.Compiled.Match(joined);
        var next = match is { } m ? (m.State, $"screen matched /{m.Pattern}/") : ((AgentState, string)?)null;
        if (next?.Item1 != _fromScreen?.State || next?.Item2 != _fromScreen?.Why)
        {
            _fromScreen = next;
        }

        // Re-evaluate even on unchanged evidence: time has passed and the quiet timer may have run out.
        Evaluate(now, "screen");
    }

    public void OnExit(int? code, DateTimeOffset now)
    {
        LastActivity = now;
        _authoritySource = null;
        Progress = null;
        ProgressIndeterminate = false;
        Set(AgentState.Exited, code is { } c ? $"process exited with code {c}" : "session ended", now);
        if (!_viewed)
        {
            Unread = true;
            AttentionSince ??= now;
        }

        AttentionRequested?.Invoke(new AgentAttention(AgentState.Exited, Explain, null, now));
    }

    /// <summary>The heartbeat: lets the quiet timer turn Working into Idle without new output.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (_isAgent && Authority == AgentAuthority.Detector && State == AgentState.Working)
        {
            Evaluate(now, "tick");
        }
    }

    // ------------------------------------------------------------------ integration inputs

    /// <summary>
    /// A hook or plugin speaks. Out-of-order reports from the same source are dropped by
    /// <paramref name="seq"/>; a different source simply takes over.
    /// </summary>
    public void OnReport(string source, long? seq, AgentState state, string? message, string? summary, string? sessionId, DateTimeOffset now)
    {
        if (State == AgentState.Exited)
        {
            // The process is gone; a hook that fires late (sessionEnd racing the exit) is stale.
            // A restarted tab gets a fresh machine.
            return;
        }

        if (string.Equals(source, _authoritySource, StringComparison.OrdinalIgnoreCase) && seq is { } s && s <= _authoritySeq)
        {
            return;
        }

        _authoritySource = source;
        _authoritySeq = seq ?? _authoritySeq;
        _authorityAt = now;
        LastActivity = now;

        if (summary is not null)
        {
            Summary = summary;
        }

        if (sessionId is not null)
        {
            SessionId = sessionId;
        }

        var reason = $"{source} reported {state}{(message is null ? string.Empty : ": " + message)}";
        switch (state)
        {
            case AgentState.Blocked or AgentState.Error:
                Set(state, reason, now);
                Unread = true;
                AttentionSince ??= now;
                AttentionRequested?.Invoke(new AgentAttention(state, reason, message, now));
                break;

            case AgentState.Working:
                _workingSince ??= now;
                Set(AgentState.Working, reason, now);
                break;

            case AgentState.Idle or AgentState.Done:
                if (State == AgentState.Done && !_viewed)
                {
                    // Still unseen: a second "idle" (OpenCode sends session.status and
                    // session.idle for one turn) must not clear the mark.
                    Explain = reason;
                    break;
                }

                var wasWorking = State is AgentState.Working or AgentState.Blocked;
                if (!_viewed && (wasWorking || state == AgentState.Done))
                {
                    Set(AgentState.Done, reason, now);
                    Unread = true;
                    AttentionSince ??= now;
                    AttentionRequested?.Invoke(new AgentAttention(AgentState.Done, reason, message, now));
                }
                else
                {
                    Set(AgentState.Idle, reason, now);
                }

                _workingSince = null;
                break;

            case AgentState.Exited:
                OnRelease(source, now);
                break;

            default:
                Set(state, reason, now);
                break;
        }
    }

    /// <summary>The integration is gone; the detector decides again.</summary>
    public void OnRelease(string source, DateTimeOffset now)
    {
        if (!string.Equals(source, _authoritySource, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _authoritySource = null;
        _authoritySeq = long.MinValue;
        if (State != AgentState.Exited)
        {
            Evaluate(now, "integration released");
        }
    }

    /// <summary>
    /// A one-shot report with no continuing authority (Codex's <c>notify</c> fires once per
    /// turn and never says "working"). Applied like a notification the detector trusts:
    /// it marks the moment and remembers the session, and the detector goes on deciding.
    /// Ignored while a real integration holds authority — it knows better.
    /// </summary>
    public void OnAdvisory(string source, AgentState state, string? message, string? summary, string? sessionId, DateTimeOffset now)
    {
        LastActivity = now;
        if (State == AgentState.Exited || Authority == AgentAuthority.Integration)
        {
            return;
        }

        if (summary is not null)
        {
            Summary = summary;
        }

        if (sessionId is not null)
        {
            SessionId = sessionId;
        }

        var reason = $"{source} said {state}{(message is null ? string.Empty : ": " + message)}";
        switch (state)
        {
            case AgentState.Blocked or AgentState.Error:
                Set(state, reason, now);
                Unread = true;
                AttentionSince ??= now;
                AttentionRequested?.Invoke(new AgentAttention(state, reason, message, now));
                break;

            case AgentState.Working:
                _workingSince ??= now;
                Set(AgentState.Working, reason, now);
                break;

            case AgentState.Idle or AgentState.Done:
                if (State == AgentState.Done && !_viewed)
                {
                    Explain = reason;
                    break;
                }

                // The turn ended: what the detector inferred from output is superseded, and the
                // quiet timer must not turn a fresh Done back into Idle.
                _fromTitle = _fromScreen = _fromProgress = null;
                _lastOutput = null;
                _firstOutputInBurst = null;
                if (!_viewed)
                {
                    _workingSince = null;
                    Set(AgentState.Done, reason, now);
                    Unread = true;
                    AttentionSince ??= now;
                    AttentionRequested?.Invoke(new AgentAttention(AgentState.Done, reason, message ?? summary, now));
                }
                else
                {
                    _workingSince = null;
                    Set(AgentState.Idle, reason, now);
                }

                break;
        }
    }

    // ------------------------------------------------------------------ evaluation

    private void Evaluate(DateTimeOffset now, string trigger)
    {
        if (!_isAgent || State == AgentState.Exited || Authority == AgentAuthority.Integration)
        {
            return;
        }

        var (state, why) = Decide(now);
        if (state is null)
        {
            return;
        }

        if (state == AgentState.Working)
        {
            _workingSince ??= now;
        }

        if (state == AgentState.Idle)
        {
            var wasWorking = State is AgentState.Working or AgentState.Blocked;
            var workedEnough = _workingSince is { } since && now - since >= MinimumWorkForDone;
            var explicitIdle = _fromTitle?.State == AgentState.Idle || _fromScreen?.State == AgentState.Idle || _fromProgress?.State == AgentState.Idle;

            if (!_viewed && wasWorking && (workedEnough || explicitIdle))
            {
                _workingSince = null;
                Set(AgentState.Done, why + " (while not viewed)", now);
                Unread = true;
                AttentionSince ??= now;
                AttentionRequested?.Invoke(new AgentAttention(AgentState.Done, why, Summary, now));
                return;
            }

            if (State == AgentState.Done && !_viewed)
            {
                // Stay Done until viewed; nothing new to say.
                return;
            }

            _workingSince = null;
        }

        if (state is AgentState.Blocked or AgentState.Error)
        {
            var isNew = State != state;
            Set(state.Value, why, now);
            if (isNew)
            {
                Unread = true;
                AttentionSince ??= now;
                AttentionRequested?.Invoke(new AgentAttention(state.Value, why, Summary, now));
            }

            return;
        }

        if (state == AgentState.Done && State != AgentState.Done)
        {
            Set(AgentState.Done, why, now);
            Unread = true;
            AttentionSince ??= now;
            AttentionRequested?.Invoke(new AgentAttention(AgentState.Done, why, Summary, now));
            return;
        }

        Set(state.Value, why, now);
    }

    /// <summary>The detector's verdict from current evidence, or null for "no opinion".</summary>
    private (AgentState? State, string Why) Decide(DateTimeOffset now)
    {
        // Explicit signals first, strictest first. A blocked match anywhere wins.
        foreach (var source in new[] { _fromScreen, _fromTitle, _fromProgress })
        {
            if (source is { State: AgentState.Blocked } b)
            {
                return (AgentState.Blocked, b.Why);
            }
        }

        foreach (var source in new[] { _fromTitle, _fromScreen, _fromProgress })
        {
            if (source is { State: AgentState.Error } e)
            {
                return (AgentState.Error, e.Why);
            }
        }

        foreach (var source in new[] { _fromTitle, _fromProgress, _fromScreen })
        {
            if (source is { State: AgentState.Working } w)
            {
                return (AgentState.Working, w.Why);
            }
        }

        foreach (var source in new[] { _fromTitle, _fromProgress, _fromScreen })
        {
            if (source is { State: AgentState.Done } d)
            {
                return (AgentState.Done, d.Why);
            }
        }

        foreach (var source in new[] { _fromTitle, _fromScreen, _fromProgress })
        {
            if (source is { State: AgentState.Idle } i)
            {
                return (AgentState.Idle, i.Why);
            }
        }

        // No explicit opinion: activity decides.
        if (_lastOutput is { } last)
        {
            var quiet = now - last;
            if (quiet >= TimeSpan.FromMilliseconds(_rules.IdleAfterMs))
            {
                return State == AgentState.Working || State == AgentState.Unknown
                    ? (AgentState.Idle, $"quiet for {quiet.TotalSeconds:F1}s")
                    : (null, string.Empty);
            }

            if (_firstOutputInBurst is { } first && last - first >= ActivityWorkingAfter)
            {
                return (AgentState.Working, "output activity");
            }
        }

        return (null, string.Empty);
    }

    /// <summary>
    /// A one-line summary from the screen, only when the rules say where it is. Without a
    /// pattern the bottom row is usually a prompt or an input box — noise, not a summary.
    /// </summary>
    private string? ExtractSummary(IReadOnlyList<string> rows)
    {
        if (_rules.SummaryRegex is not { } regex)
        {
            return null;
        }

        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var m = regex.Match(rows[i]);
            if (m.Success)
            {
                var value = (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
                return value.Length > 0 ? Truncate(value) : null;
            }
        }

        return null;
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..117] + "…";

    private void Set(AgentState state, string reason, DateTimeOffset now)
    {
        Explain = reason;
        if (state == State)
        {
            return;
        }

        var from = State;
        State = state;

        if (state is not (AgentState.Blocked or AgentState.Error or AgentState.Done))
        {
            Unread = false;
            AttentionSince = null;
        }

        if (state == AgentState.Working)
        {
            _workingSince ??= now;
        }

        Transitioned?.Invoke(new AgentTransition(from, state, reason, now));
    }
}
