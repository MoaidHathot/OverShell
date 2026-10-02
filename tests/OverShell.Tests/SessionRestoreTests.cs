using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Settings;
using Xunit;

namespace OverShell.Tests;

/// <summary>P4 (DESIGN.md §12.13): the restore planner, window placement, history.</summary>
public class SessionRestoreTests
{
    private static readonly AgentRules Rules = AgentRules.LoadDefaults();
    private static readonly SessionSettings Default = new();

    private static SavedTab Running(string harness, string? sessionId = null, string? resumeCommand = null) =>
        new() { ProfileId = "{p}", Harness = harness, AgentRunning = true, SessionId = sessionId, ResumeCommand = resumeCommand };

    [Fact]
    public void Shell_profile_types_the_saved_resume_command()
    {
        var plan = SessionRestore.Plan(Running("opencode", "ses_1", "opencode --session ses_1"), "pwsh.exe -NoLogo", Rules, Default);

        Assert.Equal(ResumeMode.Typed, plan.Mode);
        Assert.Equal("opencode --session ses_1", plan.Command);
    }

    [Fact]
    public void Dedicated_agent_profile_is_relaunched_with_the_resume_arguments_not_typed()
    {
        // Typing "opencode --session …" into a running OpenCode lands in its prompt box as text.
        var plan = SessionRestore.Plan(Running("opencode", "ses_1", "opencode --session ses_1"), @"C:\Users\me\AppData\Roaming\npm\opencode.cmd", Rules, Default);

        Assert.Equal(ResumeMode.Relaunch, plan.Mode);
        Assert.Equal(@"C:\Users\me\AppData\Roaming\npm\opencode.cmd --session ses_1", plan.Command);

        var quoted = SessionRestore.Plan(Running("copilot", "abc", "copilot --resume=abc"), "\"C:\\Program Files\\GitHub Copilot\\copilot.exe\" --allow-all-tools", Rules, Default);
        Assert.Equal(ResumeMode.Relaunch, quoted.Mode);
        Assert.Equal("\"C:\\Program Files\\GitHub Copilot\\copilot.exe\" --allow-all-tools --resume=abc", quoted.Command);

        var codex = SessionRestore.Plan(Running("codex", "thr_1", "codex resume thr_1"), "codex.exe", Rules, Default);
        Assert.Equal("codex.exe resume thr_1", codex.Command);
    }

    [Fact]
    public void Without_an_id_the_most_recent_session_form_is_used_when_allowed()
    {
        var typed = SessionRestore.Plan(Running("opencode"), "pwsh.exe", Rules, Default);
        Assert.Equal(ResumeMode.Typed, typed.Mode);
        Assert.Equal("opencode --continue", typed.Command);

        var relaunched = SessionRestore.Plan(Running("copilot"), "copilot.exe", Rules, Default);
        Assert.Equal(ResumeMode.Relaunch, relaunched.Mode);
        Assert.Equal("copilot.exe --continue", relaunched.Command);

        var off = SessionRestore.Plan(Running("opencode"), "pwsh.exe", Rules, new SessionSettings { ResumeWithoutId = false });
        Assert.Equal(ResumeMode.None, off.Mode);
        Assert.Contains("resumeWithoutId", off.Reason);
    }

    [Fact]
    public void A_saved_id_without_a_saved_command_uses_the_rule_pattern()
    {
        var plan = SessionRestore.Plan(Running("claude", "s-9"), "pwsh.exe", Rules, Default);

        Assert.Equal(ResumeMode.Typed, plan.Mode);
        Assert.Equal("claude --resume s-9", plan.Command);
    }

    [Fact]
    public void Nothing_is_resumed_when_off_when_no_agent_ran_or_when_the_profile_wraps_the_agent()
    {
        Assert.Equal(ResumeMode.None, SessionRestore.Plan(Running("opencode", "s", "opencode --session s"), "pwsh.exe", Rules, new SessionSettings { ResumeAgents = false }).Mode);
        Assert.Equal(ResumeMode.None, SessionRestore.Plan(new SavedTab { Harness = "opencode", AgentRunning = false, ResumeCommand = "opencode --session s" }, "pwsh.exe", Rules, Default).Mode);

        // `pwsh -NoExit -Command opencode`: the agent is an argument of a shell; neither typing nor relaunching is safe.
        var wrapper = SessionRestore.Plan(Running("opencode", "s", "opencode --session s"), "pwsh.exe -NoExit -Command opencode", Rules, Default);
        Assert.Equal(ResumeMode.None, wrapper.Mode);
        Assert.Contains("wraps", wrapper.Reason);

        // A profile for one harness whose tab ran another is not guessed at.
        Assert.Equal(ResumeMode.None, SessionRestore.Plan(Running("copilot", "s", "copilot --resume=s"), "opencode.exe", Rules, Default).Mode);

        // The generic harness has no resume form at all.
        Assert.Equal(ResumeMode.None, SessionRestore.Plan(Running("generic"), "pwsh.exe", Rules, Default).Mode);
    }

    [Fact]
    public void The_most_recent_form_is_used_once_per_harness_preferring_the_active_tab()
    {
        // Three id-less OpenCode tabs would all reopen the same session (OpenCode's --continue is machine-wide).
        var tabs = new List<SavedTab> { Running("opencode"), Running("opencode"), Running("copilot"), Running("opencode", "ses_9", "opencode --session ses_9") };
        var plans = SessionRestore.PlanAll(tabs, activeIndex: 1, _ => "pwsh.exe", Rules, Default);

        Assert.Equal(ResumeMode.None, plans[0].Mode);
        Assert.Contains("already resumes", plans[0].Reason);
        Assert.Equal("opencode --continue", plans[1].Command);   // the active tab wins
        Assert.Equal("copilot --continue", plans[2].Command);    // another harness is unaffected
        Assert.Equal("opencode --session ses_9", plans[3].Command); // a known id is never deduplicated

        var noActive = SessionRestore.PlanAll(tabs, activeIndex: -1, _ => "pwsh.exe", Rules, Default);
        Assert.Equal("opencode --continue", noActive[0].Command);
        Assert.Equal(ResumeMode.None, noActive[1].Mode);
    }

    [Fact]
    public void First_token_handles_quotes_and_whitespace()
    {
        Assert.Equal("pwsh.exe", SessionRestore.FirstToken("  pwsh.exe -NoLogo"));
        Assert.Equal("\"C:\\Program Files\\x\\a.exe\"", SessionRestore.FirstToken("\"C:\\Program Files\\x\\a.exe\" --flag"));
        Assert.Equal("opencode", SessionRestore.FirstToken("opencode"));
        Assert.Equal(string.Empty, SessionRestore.FirstToken("   "));
        Assert.Equal(string.Empty, SessionRestore.FirstToken(null));
    }

    [Fact]
    public void Every_bundled_harness_but_generic_has_both_resume_forms()
    {
        foreach (var set in Rules.All.Where(r => r.Id != AgentRules.GenericId))
        {
            Assert.False(string.IsNullOrWhiteSpace(set.ResumeCommand), set.Id);
            Assert.Contains(SessionRestore.SessionIdPlaceholder, set.ResumeCommand);
            Assert.False(string.IsNullOrWhiteSpace(set.ResumeLastCommand), set.Id);
            Assert.Equal(set.Id, Rules.DetectFromCommandline(SessionRestore.FirstToken(set.ResumeLastCommand)));
        }

        Assert.Null(Rules.Generic.ResumeLastCommand);
    }

    [Fact]
    public void Session_settings_defaults_match_the_shipped_file()
    {
        var settings = AppSettings.LoadDefaults();

        Assert.True(settings.Session.Restore);
        Assert.True(settings.Session.ResumeAgents);
        Assert.True(settings.Session.ResumeWithoutId);
        Assert.True(settings.Session.RestoreWindows);
        Assert.False(settings.Session.RestartWithWindows);
        Assert.Empty(settings.Problems);
    }
}
