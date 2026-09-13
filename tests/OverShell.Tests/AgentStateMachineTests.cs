using OverShell.Core.Agents;
using Xunit;

namespace OverShell.Tests;

public class AgentStateMachineTests
{
    private static readonly AgentRules Rules = AgentRules.LoadDefaults();
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static AgentStateMachine Claude(bool viewed = true)
    {
        var m = new AgentStateMachine(Rules.Find("claude")!, isAgent: true);
        m.SetViewed(viewed, T0);
        return m;
    }

    /// <summary>Feeds output every 100 ms across a window, the way an agent streams text.</summary>
    private static void Stream(AgentStateMachine m, double fromSeconds, double toSeconds)
    {
        for (var t = fromSeconds; t <= toSeconds; t += 0.1)
        {
            m.OnOutput(At(t));
        }
    }

    [Fact]
    public void Starts_unknown_and_shells_never_become_working()
    {
        var shell = new AgentStateMachine(Rules.Generic, isAgent: false);
        Stream(shell, 0, 5);
        shell.OnScreen(["Do you want to proceed? (y/n)"], At(5));
        Assert.Equal(AgentState.Unknown, shell.State);
    }

    [Fact]
    public void Sustained_output_means_working_and_quiet_means_idle_when_viewed()
    {
        var m = Claude(viewed: true);
        var transitions = new List<AgentTransition>();
        m.Transitioned += transitions.Add;

        Stream(m, 0, 1);
        Assert.Equal(AgentState.Working, m.State);

        m.Tick(At(3));
        Assert.Equal(AgentState.Idle, m.State);
        Assert.Contains("quiet", m.Explain);
        Assert.False(m.Unread);
        Assert.Equal([AgentState.Working, AgentState.Idle], transitions.Select(t => t.To));
    }

    [Fact]
    public void Turn_ending_while_not_viewed_becomes_done_and_stays_until_viewed()
    {
        var m = Claude(viewed: false);
        var attention = new List<AgentAttention>();
        m.AttentionRequested += attention.Add;

        Stream(m, 0, 4); // long enough to be a real turn
        Assert.Equal(AgentState.Working, m.State);

        m.Tick(At(6));
        Assert.Equal(AgentState.Done, m.State);
        Assert.True(m.Unread);
        Assert.True(m.NeedsAttention);
        Assert.Single(attention);
        Assert.Equal(AgentState.Done, attention[0].State);

        // More heartbeat while still unviewed: stays Done, no repeat notification.
        m.Tick(At(20));
        Assert.Equal(AgentState.Done, m.State);
        Assert.Single(attention);

        m.SetViewed(true, At(21));
        Assert.Equal(AgentState.Idle, m.State);
        Assert.False(m.Unread);
        Assert.False(m.NeedsAttention);
    }

    [Fact]
    public void A_short_banner_in_a_background_tab_is_not_done()
    {
        var m = Claude(viewed: false);
        var attention = new List<AgentAttention>();
        m.AttentionRequested += attention.Add;

        Stream(m, 0, 1); // 1 s of banner
        m.Tick(At(3));

        Assert.Equal(AgentState.Idle, m.State);
        Assert.Empty(attention);
    }

    [Fact]
    public void Explicit_idle_title_after_working_is_done_even_for_a_short_turn()
    {
        var m = Claude(viewed: false);
        m.OnTitle("⠐ fixing tests", At(0));
        Assert.Equal(AgentState.Working, m.State);

        m.OnTitle("✳ fixing tests", At(1));
        Assert.Equal(AgentState.Done, m.State);
        Assert.True(m.Unread);
    }

    [Fact]
    public void Blocked_comes_only_from_an_explicit_match_and_wins_over_working()
    {
        var m = Claude(viewed: true);
        var attention = new List<AgentAttention>();
        m.AttentionRequested += attention.Add;

        m.OnTitle("⠂ editing", At(0));
        Stream(m, 0, 2);
        Assert.Equal(AgentState.Working, m.State);

        m.OnScreen(["Bash(rm -rf build)", "Do you want to proceed?", "❯ 1. Yes", "  2. Yes, and don't ask again for rm", "  3. No, and tell Claude what to do differently (esc)"], At(2.2));
        Assert.Equal(AgentState.Blocked, m.State);
        Assert.Contains("screen matched", m.Explain);
        Assert.Single(attention);

        // Same prompt still on screen: no second alarm.
        m.OnScreen(["Do you want to proceed?", "❯ 1. Yes"], At(2.6));
        Assert.Single(attention);

        // Prompt answered, work resumes.
        m.OnScreen(["✻ Running… (3s)", "  Esc to interrupt"], At(3));
        Assert.Equal(AgentState.Working, m.State);
    }

    [Fact]
    public void Silence_never_produces_blocked()
    {
        var m = Claude(viewed: false);
        Stream(m, 0, 5);
        m.Tick(At(60));
        Assert.NotEqual(AgentState.Blocked, m.State);
    }

    [Fact]
    public void OpenCode_progress_and_title_icons_drive_state()
    {
        var oc = new AgentStateMachine(Rules.Find("opencode")!, isAgent: true);
        oc.SetViewed(false, T0);

        oc.OnProgress(3, 0, At(0));
        Assert.Equal(AgentState.Working, oc.State);
        Assert.True(oc.ProgressIndeterminate);

        oc.OnTitle("❓ OC | repo | task", At(1));
        oc.OnProgress(1, 50, At(1));
        Assert.Equal(AgentState.Blocked, oc.State);
        Assert.Equal(0.5, oc.Progress);

        oc.OnTitle("OC | repo | task", At(2));
        oc.OnProgress(3, 0, At(2));
        Assert.Equal(AgentState.Working, oc.State);

        oc.OnTitle("🔔 OC | repo | task", At(3));
        oc.OnProgress(4, 100, At(3));
        Assert.Equal(AgentState.Done, oc.State);
        Assert.True(oc.Unread);

        oc.SetViewed(true, At(4));
        Assert.Equal(AgentState.Idle, oc.State);
    }

    [Fact]
    public void Integration_is_authoritative_and_detector_evidence_is_ignored_meanwhile()
    {
        var m = new AgentStateMachine(Rules.Find("opencode")!, isAgent: true);
        m.SetViewed(false, T0);

        m.OnReport("opencode", 1, AgentState.Working, null, "fix tests", "ses_1", At(0));
        Assert.Equal(AgentAuthority.Integration, m.Authority);
        Assert.Equal(AgentState.Working, m.State);
        Assert.Equal("fix tests", m.Summary);
        Assert.Equal("ses_1", m.SessionId);

        // Screen and quiet time say otherwise; the integration wins.
        m.OnScreen(["Permission required", "Allow once"], At(1));
        m.Tick(At(30));
        Assert.Equal(AgentState.Working, m.State);

        m.OnReport("opencode", 2, AgentState.Blocked, "Action requires your approval", null, null, At(31));
        Assert.Equal(AgentState.Blocked, m.State);
        Assert.Contains("approval", m.Explain);

        m.OnReport("opencode", 3, AgentState.Idle, "turn finished", null, null, At(40));
        Assert.Equal(AgentState.Done, m.State); // not viewed
        Assert.True(m.Unread);
    }

    [Fact]
    public void Stale_sequence_numbers_from_the_same_source_are_dropped()
    {
        var m = new AgentStateMachine(Rules.Find("opencode")!, isAgent: true);
        m.OnReport("opencode", 5, AgentState.Working, null, null, null, At(0));
        m.OnReport("opencode", 4, AgentState.Idle, null, null, null, At(1));
        Assert.Equal(AgentState.Working, m.State);

        m.OnReport("opencode", 6, AgentState.Idle, null, null, null, At(2));
        Assert.Equal(AgentState.Idle, m.State);
    }

    [Fact]
    public void Release_hands_control_back_to_the_detector()
    {
        var m = Claude(viewed: true);
        m.OnReport("claude-hooks", 1, AgentState.Working, null, null, null, At(0));
        Assert.Equal(AgentAuthority.Integration, m.Authority);

        m.OnRelease("claude-hooks", At(1));
        Assert.Equal(AgentAuthority.Detector, m.Authority);

        m.OnTitle("✳ Claude Code", At(2));
        Assert.Equal(AgentState.Idle, m.State);
    }

    [Fact]
    public void Exit_is_terminal_and_wins_over_everything()
    {
        var m = Claude(viewed: false);
        m.OnReport("claude-hooks", 1, AgentState.Working, null, null, null, At(0));
        m.OnExit(0, At(1));
        Assert.Equal(AgentState.Exited, m.State);
        Assert.Equal(AgentAuthority.Detector, m.Authority);

        m.OnReport("claude-hooks", 2, AgentState.Working, null, null, null, At(2));
        m.OnTitle("⠐ x", At(3));
        Stream(m, 3, 5);
        Assert.Equal(AgentState.Exited, m.State);
    }

    [Fact]
    public void Bell_in_an_unviewed_shell_marks_done_once()
    {
        var shell = new AgentStateMachine(Rules.Generic, isAgent: false);
        shell.SetViewed(false, T0);
        var attention = new List<AgentAttention>();
        shell.AttentionRequested += attention.Add;

        shell.OnBell(At(0));
        shell.OnBell(At(0.5)); // debounced
        Assert.Equal(AgentState.Done, shell.State);
        Assert.Single(attention);

        // A shell rests at Unknown, not Idle: "idle" is an agent's word.
        shell.SetViewed(true, At(1));
        Assert.Equal(AgentState.Unknown, shell.State);
    }

    [Fact]
    public void Long_shell_command_finishing_in_the_background_marks_done()
    {
        var shell = new AgentStateMachine(Rules.Generic, isAgent: false);
        shell.SetViewed(false, T0);
        shell.OnPromptMark('C', At(0));
        shell.OnPromptMark('D', At(2));
        Assert.Equal(AgentState.Unknown, shell.State); // too short to matter

        shell.OnPromptMark('C', At(10));
        shell.OnPromptMark('D', At(30));
        Assert.Equal(AgentState.Done, shell.State);
    }

    [Fact]
    public void Notification_body_matching_blocked_words_blocks_even_when_viewed()
    {
        var m = new AgentStateMachine(Rules.Find("codex")!, isAgent: true);
        m.SetViewed(true, T0);
        var attention = new List<AgentAttention>();
        m.AttentionRequested += attention.Add;

        m.OnNotification(null, "Codex: approval requested for `rm -rf build`", At(0));
        Assert.Equal(AgentState.Blocked, m.State);
        Assert.Single(attention);
        Assert.Contains("approval", attention[0].Message);
    }

    [Fact]
    public void Switching_to_shell_rules_clears_agent_evidence()
    {
        var m = Claude(viewed: true);
        m.OnTitle("⠐ working", At(0));
        Assert.Equal(AgentState.Working, m.State);

        m.SetRules(Rules.Generic, isAgent: false, At(1));
        Assert.Equal(AgentState.Unknown, m.State);
        Assert.False(m.IsAgent);
    }

    [Fact]
    public void A_repeated_idle_report_does_not_clear_an_unseen_done()
    {
        // OpenCode emits session.status idle and session.idle for one turn.
        var m = new AgentStateMachine(Rules.Find("opencode")!, isAgent: true);
        m.SetViewed(false, T0);
        m.OnReport("opencode", 1, AgentState.Working, null, null, null, At(0));
        m.OnReport("opencode", 2, AgentState.Idle, "turn finished", null, null, At(10));
        Assert.Equal(AgentState.Done, m.State);

        m.OnReport("opencode", 3, AgentState.Idle, "turn finished", null, null, At(10.01));
        Assert.Equal(AgentState.Done, m.State);
        Assert.True(m.Unread);

        m.SetViewed(true, At(20));
        Assert.Equal(AgentState.Idle, m.State);
    }

    [Fact]
    public void An_unseen_done_survives_the_agent_leaving_and_clears_to_unknown_for_a_shell()
    {
        // `opencode run` finished in a background tab; the process is gone and the tab is a
        // shell again — the user still has to be told it finished.
        var m = new AgentStateMachine(Rules.Find("opencode")!, isAgent: true);
        m.SetViewed(false, T0);
        m.OnReport("opencode", 1, AgentState.Working, null, null, null, At(0));
        m.OnReport("opencode", 2, AgentState.Idle, "turn finished", null, null, At(10));
        Assert.Equal(AgentState.Done, m.State);

        m.OnRelease("opencode", At(15));
        m.SetRules(Rules.Generic, isAgent: false, At(15));
        Assert.Equal(AgentState.Done, m.State);
        Assert.True(m.Unread);
        Assert.Equal(AgentAuthority.Detector, m.Authority);

        m.SetViewed(true, At(30));
        Assert.Equal(AgentState.Unknown, m.State);
        Assert.False(m.Unread);
    }

    [Fact]
    public void Summary_comes_from_the_status_line_when_the_rules_say_where()
    {
        var m = Claude(viewed: true);
        m.OnScreen(["some output", "✻ Thinking… (12s · ↓ 1.2k tokens)", "  Esc to interrupt"], At(0));
        Assert.Equal("Thinking… (12s · ↓ 1.2k tokens)", m.Summary);
    }
}
