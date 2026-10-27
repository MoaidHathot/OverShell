using System.Text.Json.Nodes;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;
using OverShell.Core.Settings;
using OverShell.Core.Tabs;
using Xunit;

namespace OverShell.Tests;

public class TabLabelTests
{
    [Theory]
    [InlineData("🔔 OC | repo | Shopping list bug fixes", "Shopping list bug fixes")]
    [InlineData("OC | repo | Keep this | separator", "Keep this | separator")]
    [InlineData("OpenCode - Animation performance", "Animation performance")]
    [InlineData("OpenCode: 日本語 🚀", "日本語 🚀")]
    [InlineData("OpenCode - A: compare options", "A: compare options")]
    [InlineData("Debug app.exe", "Debug app.exe")]
    [InlineData("OpenCode", null)]
    [InlineData("OC | repo | New session - 2026-10-27T00:00:00Z", null)]
    [InlineData("OC | repo", null)]
    [InlineData("C:\\tools\\opencode.exe", null)]
    [InlineData("❓", null)]
    public void Terminal_titles_supply_task_names_without_state_or_harness_noise(string title, string? expected) =>
        Assert.Equal(expected, TabLabels.FromTerminal(title, "opencode", "OpenCode", "PowerShell", "repo"));

    [Fact]
    public void Custom_names_that_look_like_a_new_session_prefix_are_not_discarded() =>
        Assert.Equal("New session - onboarding", TabLabels.SessionName("New session - onboarding"));

    [Theory]
    [InlineData(TabLabelMode.Session, "Task")]
    [InlineData(TabLabelMode.Project, "repo — Task")]
    [InlineData(TabLabelMode.Harness, "OpenCode")]
    public void Modes_choose_the_expected_primary_label(TabLabelMode mode, string expected) =>
        Assert.Equal(expected, TabLabels.Primary(mode, true, "Task", "Terminal task", "repo", "OpenCode", "PowerShell"));

    [Fact]
    public void Missing_task_metadata_falls_back_to_terminal_project_then_harness()
    {
        Assert.Equal("Terminal task", TabLabels.Primary(TabLabelMode.Session, true, null, "Terminal task", "repo", "OpenCode", "PowerShell"));
        Assert.Equal("repo", TabLabels.Primary(TabLabelMode.Session, true, null, null, "repo", "OpenCode", "PowerShell"));
        Assert.Equal("OpenCode", TabLabels.Primary(TabLabelMode.Session, true, null, null, "", "OpenCode", "PowerShell"));
        Assert.Equal("PowerShell", TabLabels.Primary(TabLabelMode.Session, false, null, null, "repo", "", "PowerShell"));
    }

    [Fact]
    public void Same_task_in_different_projects_uses_project_qualifiers()
    {
        TabLabelEntry[] tabs = [new("one", "aaaa1111", "Fix bug", "alpha", false), new("two", "bbbb2222", "Fix bug", "beta", false)];
        var result = TabLabels.Prefixes(tabs);
        Assert.Equal("alpha — ", result["one"]);
        Assert.Equal("beta — ", result["two"]);
    }

    [Fact]
    public void Same_project_duplicates_have_visible_stable_distinct_identifiers()
    {
        TabLabelEntry[] tabs = [new("one", "aaaa1111", "A very long identical task name", "repo", false), new("two", "aaaa2222", "A very long identical task name", "repo", false)];
        var result = TabLabels.Prefixes(tabs);
        Assert.StartsWith("[aaaa1]", result["one"]);
        Assert.StartsWith("[aaaa2]", result["two"]);
        Assert.Equal(result["one"], TabLabels.Prefixes(tabs.Reverse().ToArray())["one"]);
        Assert.Equal("", TabLabels.Prefixes([tabs[0]])["one"]);
    }

    [Fact]
    public void Manual_names_are_preserved_even_when_they_resemble_an_automatic_qualifier()
    {
        TabLabelEntry[] tabs = [new("one", "aaaa1111", "Task", "repo", false), new("two", "bbbb2222", "Task", "repo", false), new("manual", "cccc3333", "[aaaa] repo — Task", "repo", true)];
        var prefixes = TabLabels.Prefixes(tabs);
        Assert.Equal("", prefixes["manual"]);
        Assert.Equal(tabs.Length, tabs.Select(t => prefixes[t.Key] + t.Text).Distinct().Count());
    }

    [Fact]
    public void Unique_sessions_in_the_same_directory_do_not_get_extra_noise()
    {
        TabLabelEntry[] tabs = [new("one", "aaaa1111", "Shopping list", "repo", false), new("two", "bbbb2222", "Animation", "repo", false)];
        Assert.All(TabLabels.Prefixes(tabs).Values, prefix => Assert.Empty(prefix));
    }

    [Fact]
    public void Defaults_catalog_and_saved_metadata_round_trip()
    {
        Assert.Equal(TabLabelMode.Session, AppSettings.LoadDefaults().Tabs.EffectiveLabelMode);
        Assert.Equal(3, SettingsCatalog.Find("tabs.labelMode")!.Choices.Count);
        var snapshot = new SessionSnapshot { Tabs = [new SavedTab { NamingId = "aaaa1111", Label = "custom", SessionId = "session-a", SessionTitle = "Task", TerminalTitle = "OC | repo | Task", Project = "repo" }] };
        var restored = Jsonc.To<SessionSnapshot>(Jsonc.Parse(Jsonc.Serialize(snapshot), out _), out _)!;
        Assert.Equal("custom", restored.Tabs[0].Label);
        Assert.Equal("Task", restored.Tabs[0].SessionTitle);
        Assert.Equal("aaaa1111", restored.Tabs[0].NamingId);
    }

    [Fact]
    public void Unknown_label_mode_falls_back_without_resetting_other_settings()
    {
        var directory = Directory.CreateTempSubdirectory("overshell-label-mode");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.jsonc");
            File.WriteAllText(path, """{"tabs":{"labelMode":"future-value","twoLine":false},"theme":"light"}""");
            var settings = AppSettings.Load(path);
            Assert.Equal(TabLabelMode.Session, settings.Tabs.EffectiveLabelMode);
            Assert.False(settings.Tabs.TwoLine);
            Assert.Equal("light", settings.Theme);
            Assert.Contains(settings.Problems, p => p.Contains("labelMode"));
        }
        finally { directory.Delete(true); }
    }
}

public class SessionTitleReportTests
{
    private static AgentStateMachine Create() => new(new AgentRuleSet { Id = "test", DisplayName = "Test" }, true);

    [Fact]
    public void Metadata_rename_updates_identity_without_finishing_a_turn()
    {
        var agent = Create();
        var now = DateTimeOffset.Now;
        agent.SetViewed(false, now);
        agent.OnReport("ui", 1, AgentState.Working, null, "Running tests", "a", now, "Original");
        var attention = new List<AgentAttention>();
        agent.AttentionRequested += attention.Add;
        agent.OnReport("ui", 2, AgentState.Unknown, null, null, "a", now.AddSeconds(1), "Renamed", metadataOnly: true);
        Assert.Equal("Renamed", agent.SessionTitle);
        Assert.Equal("Running tests", agent.Summary);
        Assert.Equal(AgentState.Working, agent.State);
        Assert.Equal(now, agent.LastActivity);
        Assert.Empty(attention);
    }

    [Fact]
    public void Switching_session_clears_previous_title_summary_and_unseen_completion()
    {
        var agent = Create();
        var now = DateTimeOffset.Now;
        agent.SetViewed(false, now);
        agent.OnReport("ui", 1, AgentState.Working, null, "old activity", "a", now, "Old task");
        agent.OnReport("ui", 2, AgentState.Idle, null, null, "b", now.AddSeconds(1), sessionKnown: true);
        Assert.Equal("b", agent.SessionId);
        Assert.Null(agent.SessionTitle);
        Assert.Null(agent.Summary);
        Assert.Equal(AgentState.Idle, agent.State);
        Assert.False(agent.Unread);
    }

    [Fact]
    public void Stale_reports_cannot_undo_a_rename_or_session_switch()
    {
        var agent = Create();
        var now = DateTimeOffset.Now;
        agent.OnReport("ui", 4, AgentState.Idle, null, null, "b", now, "Current");
        Assert.False(agent.OnReport("ui", 3, AgentState.Blocked, null, null, "a", now, "Old"));
        Assert.Equal("b", agent.SessionId);
        Assert.Equal("Current", agent.SessionTitle);
        Assert.Equal(AgentState.Idle, agent.State);
    }

    [Fact]
    public void Metadata_wire_format_can_explicitly_clear_the_visible_session()
    {
        var report = IntegrationReport.Parse("tab", JsonNode.Parse("""{"source":"ui","seq":2,"metadataOnly":true,"session":{"id":null,"title":null},"cwd":"C:\\repo"}"""), out var error)!;
        Assert.Null(error);
        Assert.True(report.SessionKnown && report.MetadataOnly);
        Assert.Equal("", report.SessionTitle);
        Assert.Equal(@"C:\repo", report.WorkingDirectory);
        var agent = Create();
        agent.OnReport("ui", 1, AgentState.Idle, null, null, "a", DateTimeOffset.Now, "Task");
        agent.OnReport(report.Source, report.Seq, report.State, report.Message, report.Summary, report.SessionId, DateTimeOffset.Now, report.SessionTitle, report.SessionKnown, report.MetadataOnly);
        Assert.Null(agent.SessionId);
        Assert.Null(agent.SessionTitle);
    }

    [Fact]
    public void Saved_titles_are_temporary_until_live_metadata_arrives()
    {
        var agent = Create();
        agent.SeedSession("a", "Saved task");
        Assert.True(agent.SessionTitleIsSeeded);
        agent.OnReport("ui", 1, AgentState.Idle, null, null, "a", DateTimeOffset.Now, "Live task");
        Assert.False(agent.SessionTitleIsSeeded);
        Assert.Equal("Live task", agent.SessionTitle);
    }

    [Fact]
    public void Releasing_the_bridge_restores_detector_authority_and_title_fallback()
    {
        var agent = Create();
        var now = DateTimeOffset.Now;
        agent.OnReport("ui", 1, AgentState.Working, null, null, "a", now, "Task");
        agent.OnRelease("ui", now);
        Assert.Equal(AgentAuthority.Detector, agent.Authority);
        Assert.False(agent.SessionTitleKnown);
        Assert.True(agent.SessionTitleIsSeeded);
        Assert.Equal("a", agent.SessionId);
    }

    [Theory]
    [InlineData("""{"source":"ui","session":{"id":12}}""")]
    [InlineData("""{"source":"ui","session":{"title":[]}}""")]
    [InlineData("""{"source":"ui","session":"wrong-shape"}""")]
    public void Invalid_metadata_cannot_silently_clear_a_session(string json)
    {
        Assert.Null(IntegrationReport.Parse("tab", JsonNode.Parse(json), out var error));
        Assert.NotNull(error);
    }
}
