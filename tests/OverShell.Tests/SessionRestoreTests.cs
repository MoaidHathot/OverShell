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

public class SessionHistoryTests
{
    private static SessionSnapshot Snapshot(DateTimeOffset at, SessionCloseReason? reason, params string[] dirs) => new()
    {
        SavedAt = at,
        CloseReason = reason,
        Tabs = dirs.Select(d => new SavedTab { ProfileId = "{p}", WorkingDirectory = d }).ToList(),
    };

    [Fact]
    public void Archives_are_listed_newest_first_and_keep_their_close_reason()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-history");
        try
        {
            var t0 = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
            Assert.NotNull(SessionHistory.Archive(dir.FullName, Snapshot(t0, SessionCloseReason.Closed, "C:\\a"), out var e1));
            Assert.Null(e1);
            var interrupted = SessionHistory.Archive(dir.FullName, Snapshot(t0.AddMinutes(5), null, "C:\\b", "C:\\c"), out _);
            Assert.NotNull(interrupted);
            Assert.EndsWith("-interrupted.json", interrupted);

            var list = SessionHistory.List(dir.FullName);
            Assert.Equal(2, list.Count);
            Assert.True(list[0].Interrupted);
            Assert.Equal(2, list[0].Tabs.Count);
            Assert.Equal(SessionCloseReason.Closed, list[1].CloseReason);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Empty_sessions_and_repeats_of_the_newest_archive_are_not_archived()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-history");
        try
        {
            var t0 = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
            Assert.Null(SessionHistory.Archive(dir.FullName, Snapshot(t0, SessionCloseReason.Closed), out _));
            Assert.NotNull(SessionHistory.Archive(dir.FullName, Snapshot(t0, SessionCloseReason.Closed, "C:\\a"), out _));

            // Same tabs a minute later (a restart that restored everything and closed again): nothing new.
            Assert.Null(SessionHistory.Archive(dir.FullName, Snapshot(t0.AddMinutes(1), SessionCloseReason.Closed, "C:\\a"), out var error));
            Assert.Null(error);
            Assert.Single(SessionHistory.List(dir.FullName));

            // A moved window, another closed-tab entry: still the same session as far as the archive is concerned.
            var moved = Snapshot(t0.AddMinutes(2), SessionCloseReason.Closed, "C:\\a");
            var movedWithExtras = new SessionSnapshot { SavedAt = moved.SavedAt, CloseReason = moved.CloseReason, Tabs = moved.Tabs, Window = new SavedWindow { Left = 500, Top = 20, Width = 900, Height = 700 }, RecentlyClosed = [new SavedTab { ProfileId = "{p}", Label = "gone" }] };
            Assert.Null(SessionHistory.Archive(dir.FullName, movedWithExtras, out _));
            Assert.Single(SessionHistory.List(dir.FullName));

            // A different tab set is new; two archives in the same second get distinct names.
            Assert.NotNull(SessionHistory.Archive(dir.FullName, Snapshot(t0, SessionCloseReason.Closed, "C:\\b"), out _));
            Assert.Equal(2, SessionHistory.List(dir.FullName).Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Only_the_newest_ten_are_kept()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-history");
        try
        {
            var t0 = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 13; i++)
            {
                Assert.NotNull(SessionHistory.Archive(dir.FullName, Snapshot(t0.AddMinutes(i), SessionCloseReason.Closed, $"C:\\{i}"), out _));
            }

            var list = SessionHistory.List(dir.FullName);
            Assert.Equal(SessionHistory.Keep, list.Count);
            Assert.Equal("C:\\12", list[0].Tabs[0].WorkingDirectory);
            Assert.Equal("C:\\3", list[^1].Tabs[0].WorkingDirectory);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class WindowPlacementTests
{
    private static readonly Bounds Desktop = new(0, 0, 2560, 1440);

    [Fact]
    public void A_window_still_on_the_desktop_keeps_its_place()
    {
        var saved = new SavedWindow { Left = 100, Top = 50, Width = 1200, Height = 800 };
        Assert.Equal(new Bounds(100, 50, 1200, 800), WindowPlacement.Clamp(saved, Desktop, 520, 320));

        // Partly off the right edge but with more than a caption's worth visible: left alone.
        var edge = new SavedWindow { Left = 2400, Top = 1300, Width = 1200, Height = 800 };
        Assert.Equal(new Bounds(2400, 1300, 1200, 800), WindowPlacement.Clamp(edge, Desktop, 520, 320));
    }

    [Fact]
    public void A_window_on_a_monitor_that_is_gone_moves_to_the_desktop_corner()
    {
        // Saved on a second monitor to the left that is no longer attached.
        var gone = new SavedWindow { Left = -1900, Top = 100, Width = 1200, Height = 800 };
        Assert.Equal(new Bounds(0, 0, 1200, 800), WindowPlacement.Clamp(gone, Desktop, 520, 320));

        // Only a sliver visible (less than 120 px): also moved.
        var sliver = new SavedWindow { Left = 2500, Top = 100, Width = 1200, Height = 800 };
        Assert.Equal(new Bounds(0, 0, 1200, 800), WindowPlacement.Clamp(sliver, Desktop, 520, 320));

        // A desktop that moved: the virtual screen may start at negative coordinates.
        var shifted = new Bounds(-1920, 0, 4480, 1440);
        var farRight = new SavedWindow { Left = 5000, Top = 0, Width = 800, Height = 600 };
        Assert.Equal(new Bounds(-1920, 0, 800, 600), WindowPlacement.Clamp(farRight, shifted, 520, 320));
    }

    [Fact]
    public void A_window_larger_than_the_desktop_shrinks_to_it_and_bad_sizes_are_ignored()
    {
        var huge = new SavedWindow { Left = 0, Top = 0, Width = 5000, Height = 3000 };
        Assert.Equal(new Bounds(0, 0, 2560, 1440), WindowPlacement.Clamp(huge, Desktop, 520, 320));

        Assert.Null(WindowPlacement.Clamp(null, Desktop, 520, 320));
        Assert.Null(WindowPlacement.Clamp(new SavedWindow { Width = 100, Height = 100 }, Desktop, 520, 320));
        Assert.Null(WindowPlacement.Clamp(new SavedWindow { Left = double.NaN, Top = 0, Width = 800, Height = 600 }, Desktop, 520, 320));
    }
}

public class RestorePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private static SessionSnapshot Live(bool interrupted, int livedSeconds, bool stamped = true) => new()
    {
        StartedAt = stamped ? T0 : null,
        SavedAt = T0.AddSeconds(livedSeconds),
        CloseReason = interrupted ? null : SessionCloseReason.Closed,
        Tabs = [new SavedTab { ProfileId = "{p}" }],
    };

    private static ArchivedSession Archive(bool interrupted, int livedSeconds, bool stamped = true) =>
        new("x.json", T0.AddSeconds(livedSeconds), stamped ? T0 : null, interrupted, interrupted ? null : SessionCloseReason.Closed, [new SavedTab()]);

    [Fact]
    public void Two_early_interrupted_runs_in_a_row_hold_the_restore()
    {
        Assert.True(RestorePolicy.ShouldHold(Live(interrupted: true, 20), [Archive(interrupted: true, 15)]));
        Assert.True(RestorePolicy.ShouldHold(Live(interrupted: true, 59), [Archive(interrupted: true, 59), Archive(interrupted: false, 3000)]));
    }

    [Fact]
    public void One_early_death_a_long_life_or_a_clean_close_do_not()
    {
        // One strike only: the previous run before it closed cleanly.
        Assert.False(RestorePolicy.ShouldHold(Live(interrupted: true, 20), [Archive(interrupted: false, 20)]));
        Assert.False(RestorePolicy.ShouldHold(Live(interrupted: true, 20), []));

        // The last run lived long enough: a crash after an hour of work is not a loop.
        Assert.False(RestorePolicy.ShouldHold(Live(interrupted: true, 3600), [Archive(interrupted: true, 10)]));

        // The run before it lived long enough.
        Assert.False(RestorePolicy.ShouldHold(Live(interrupted: true, 20), [Archive(interrupted: true, 600)]));

        // Clean close, nothing to hold.
        Assert.False(RestorePolicy.ShouldHold(Live(interrupted: false, 20), [Archive(interrupted: true, 10)]));
        Assert.False(RestorePolicy.ShouldHold(null, [Archive(interrupted: true, 10)]));
    }

    [Fact]
    public void Files_without_a_start_stamp_never_count_as_early_deaths()
    {
        Assert.False(RestorePolicy.ShouldHold(Live(interrupted: true, 20, stamped: false), [Archive(interrupted: true, 10)]));
        Assert.False(RestorePolicy.ShouldHold(Live(interrupted: true, 20), [Archive(interrupted: true, 10, stamped: false)]));
    }
}

public class SessionScreensTests
{
    [Fact]
    public void Trim_keeps_the_bottom_rows_without_trailing_blanks()
    {
        var rows = Enumerable.Range(1, 40).Select(i => $"row {i}   ").Concat(["", "   ", ""]).ToList();
        var trimmed = SessionScreens.Trim(rows);

        Assert.Equal(SessionScreens.RowsKept, trimmed.Count);
        Assert.Equal("row 11", trimmed[0]);
        Assert.Equal("row 40", trimmed[^1]);
        Assert.Empty(SessionScreens.Trim(["", ""]));
    }

    [Fact]
    public void Preamble_dims_the_rows_strips_controls_and_ends_with_the_rule_and_a_blank_line()
    {
        var screen = new SavedScreen { Rows = ["PS C:\\> build", "ok\u001b[31m evil", "PS C:\\> "], At = DateTimeOffset.Now };
        var text = SessionScreens.Preamble(screen, "interrupted 10:21, the screen before");

        Assert.StartsWith("\u001b[2mPS C:\\> build\r\nok[31m evil\r\n", text);
        // Ordinal on purpose: a culture-aware compare treats ESC as ignorable and would find "ok" + ESC in "ok[".
        Assert.DoesNotContain("ok\u001b", text, StringComparison.Ordinal);
        Assert.EndsWith("\u001b[0m\u001b[2;3m\u2014 interrupted 10:21, the screen before \u2014\u001b[0m\r\n\r\n", text);
    }

    [Fact]
    public void Round_trips_through_the_file()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-screens");
        try
        {
            var path = Path.Combine(dir.FullName, "session-screens.json");
            var at = new DateTimeOffset(2026, 10, 3, 2, 40, 0, TimeSpan.FromHours(3));
            var file = new SessionScreens { Tabs = { ["abc"] = new SavedScreen { Rows = ["a", "b"], At = at } } };
            Assert.True(file.Save(path, out var error), error);
            Assert.False(File.Exists(path + ".tmp"));

            var loaded = SessionScreens.Load(path, out error);
            Assert.Null(error);
            Assert.Equal(["a", "b"], loaded!.Tabs["abc"].Rows);
            Assert.Equal(at, loaded.Tabs["abc"].At);
            Assert.Null(SessionScreens.Load(Path.Combine(dir.FullName, "none.json"), out _));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class WorkspaceTests
{
    [Theory]
    [InlineData("OverShell dev", "overshell-dev")]
    [InlineData("  Build & Test!!  ", "build-test")]
    [InlineData("日本語 work", "日本語-work")]
    [InlineData("---", "workspace")]
    public void Slugs_are_file_and_command_safe(string name, string slug) => Assert.Equal(slug, Workspace.Slug(name));

    [Fact]
    public void Loads_files_names_them_after_the_file_when_unnamed_and_reports_problems()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-ws");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "dev.jsonc"), """
                // a workspace
                { "view": "herd", "tabs": [ { "profile": "PowerShell", "cwd": "%USERPROFILE%", "label": "home" }, { "command": "opencode", "detached": true } ] }
                """);
            File.WriteAllText(Path.Combine(dir.FullName, "Named One.jsonc"), """{ "name": "Review", "description": "PRs", "tabs": [ { "profile": "cmd" } ] }""");
            File.WriteAllText(Path.Combine(dir.FullName, "empty.jsonc"), """{ "tabs": [] }""");
            File.WriteAllText(Path.Combine(dir.FullName, "broken.jsonc"), "{ nope");

            var catalog = WorkspaceCatalog.Load(dir.FullName);

            Assert.Equal(2, catalog.All.Count);
            var dev = catalog.Find("dev")!;
            Assert.Equal("dev", dev.Name);
            Assert.Equal("herd", dev.View);
            Assert.Equal(2, dev.Tabs.Count);
            Assert.Equal("home", dev.Tabs[0].Label);
            Assert.True(dev.Tabs[1].Detached);
            Assert.Equal("workspace.open.dev", dev.CommandId);
            Assert.Equal("Review", catalog.Find("review")!.Name);
            Assert.Equal(2, catalog.Problems.Count);
            Assert.Contains(catalog.Problems, p => p.Contains("empty.jsonc") && p.Contains("no tabs"));
            Assert.Contains(catalog.Problems, p => p.Contains("broken.jsonc"));
            Assert.Empty(WorkspaceCatalog.Load(Path.Combine(dir.FullName, "missing")).All);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Save_writes_a_commented_file_that_loads_back()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-ws");
        try
        {
            var workspace = new Workspace
            {
                Name = "OverShell dev",
                View = "terminal",
                Tabs = [new WorkspaceTab { Profile = "PowerShell", Cwd = "W:\\Github\\OverShell", Label = "build", Group = "dev" }, new WorkspaceTab { Profile = "PowerShell", Cwd = "W:\\Github\\OverShell", Command = "opencode" }],
            };

            var path = WorkspaceCatalog.Save(dir.FullName, workspace);
            Assert.Equal("overshell-dev.jsonc", Path.GetFileName(path));
            var text = File.ReadAllText(path);
            Assert.StartsWith("// OverShell workspace", text);
            Assert.Contains("\"command\": \"opencode\"", text);
            Assert.DoesNotContain("\"detached\"", text);

            var loaded = WorkspaceCatalog.Load(dir.FullName).Find("OverShell dev")!;
            Assert.Equal("OverShell dev", loaded.Name);
            Assert.Equal(path, loaded.Path);
            Assert.Equal("opencode", loaded.Tabs[1].Command);
            Assert.Equal("dev", loaded.Tabs[0].Group);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Protocol_urls_for_workspaces_reopen_and_history_parse()
    {
        var ws = OverShell.Core.Integrations.ProtocolRequest.Parse(OverShell.Core.Integrations.ProtocolRequest.WorkspaceUrl("OverShell dev"))!;
        Assert.Equal(OverShell.Core.Integrations.ProtocolAction.Workspace, ws.Action);
        Assert.Equal("OverShell dev", ws.Target);

        Assert.Equal(OverShell.Core.Integrations.ProtocolAction.Reopen, OverShell.Core.Integrations.ProtocolRequest.Parse("overshell://reopen")!.Action);
        var history = OverShell.Core.Integrations.ProtocolRequest.Parse(OverShell.Core.Integrations.ProtocolRequest.HistoryUrl("20261003-022142-interrupted"))!;
        Assert.Equal(OverShell.Core.Integrations.ProtocolAction.History, history.Action);
        Assert.Equal("20261003-022142-interrupted", history.Target);
        Assert.Null(OverShell.Core.Integrations.ProtocolRequest.Parse("overshell://history")!.Target);
    }
}