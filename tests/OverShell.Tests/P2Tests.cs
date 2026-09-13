using System.Text.Json.Nodes;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;
using OverShell.Core.Settings;
using Xunit;

namespace OverShell.Tests;

public class SessionSnapshotTests
{
    [Fact]
    public void Round_trips_tabs_view_and_layout_overrides()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-session");
        try
        {
            var path = Path.Combine(dir.FullName, "session.json");
            var snapshot = new SessionSnapshot
            {
                SavedAt = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero),
                View = "herd",
                ActiveIndex = 1,
                Tabs =
                [
                    new SavedTab { ProfileId = "{p1}", WorkingDirectory = "W:\\Github\\OverShell", Label = "build", Group = "OverShell" },
                    new SavedTab { ProfileId = "{p1}", WorkingDirectory = "C:\\Users\\me", Harness = "opencode", SessionId = "ses_1", ResumeCommand = "opencode --session ses_1" },
                ],
                LayoutOverrides = { ["terminal"] = "left-list" },
            };

            Assert.True(snapshot.Save(path, out var saveError), saveError);
            Assert.False(File.Exists(path + ".tmp"));

            var loaded = SessionSnapshot.Load(path, out var loadError)!;
            Assert.Null(loadError);
            Assert.Equal("herd", loaded.View);
            Assert.Equal(1, loaded.ActiveIndex);
            Assert.Equal(2, loaded.Tabs.Count);
            Assert.Equal("build", loaded.Tabs[0].Label);
            Assert.Equal("OverShell", loaded.Tabs[0].Group);
            Assert.Equal("opencode --session ses_1", loaded.Tabs[1].ResumeCommand);
            Assert.Equal("left-list", loaded.LayoutOverrides["terminal"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Missing_and_corrupt_files_do_not_throw()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-session");
        try
        {
            Assert.Null(SessionSnapshot.Load(Path.Combine(dir.FullName, "none.json"), out var e1));
            Assert.Null(e1);

            var bad = Path.Combine(dir.FullName, "bad.json");
            File.WriteAllText(bad, "{ not json");
            Assert.Null(SessionSnapshot.Load(bad, out var e2));
            Assert.NotNull(e2);

            var array = Path.Combine(dir.FullName, "array.json");
            File.WriteAllText(array, "[1,2,3]");
            Assert.Null(SessionSnapshot.Load(array, out _));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

public class SnippetsTests
{
    [Fact]
    public void Parses_names_texts_and_reports_bad_entries()
    {
        var problems = new List<string>();
        var list = Snippets.Parse(Jsonc.Parse("""
            [
              { "name": "Explain", "text": "Explain what you just did.", "description": "Ask for a recap" },
              { "name": "Tests", "text": "Run the tests and fix failures." },
              { "name": "Explain", "text": "duplicate" },
              { "text": "no name" },
              "not an object",
            ]
            """, out _), problems, "snippets.jsonc");

        Assert.Equal(2, list.Count);
        Assert.Equal("Ask for a recap", list[0].Description);
        Assert.Equal(3, problems.Count);
    }

    [Theory]
    [InlineData("Explain", "snippet.explain")]
    [InlineData("Run the tests!", "snippet.run-the-tests")]
    [InlineData("  a--b  ", "snippet.a-b")]
    public void Command_ids_are_stable_and_safe(string name, string expected) => Assert.Equal(expected, Snippets.CommandId(name));

    [Fact]
    public void A_missing_file_is_an_empty_list()
    {
        var problems = new List<string>();
        Assert.Empty(Snippets.Load(Path.Combine(Path.GetTempPath(), "overshell-no-such-snippets.jsonc"), problems));
        Assert.Empty(problems);
    }
}

public class ProtocolRequestTests
{
    [Theory]
    [InlineData("overshell://focus/ab12cd34", ProtocolAction.Focus, "ab12cd34")]
    [InlineData("OVERSHELL://Focus/ab12cd34/", ProtocolAction.Focus, "ab12cd34")]
    [InlineData("overshell://view/herd", ProtocolAction.View, "herd")]
    [InlineData("overshell://view/Dashboard", ProtocolAction.View, "dashboard")]
    [InlineData("overshell://new", ProtocolAction.New, null)]
    [InlineData("overshell://", ProtocolAction.Show, null)]
    [InlineData("overshell://unknown/thing", ProtocolAction.Show, null)]
    public void Parses_actions_and_targets(string url, ProtocolAction action, string? target)
    {
        var request = ProtocolRequest.Parse(url)!;
        Assert.Equal(action, request.Action);
        Assert.Equal(target, request.Target);
    }

    [Fact]
    public void Query_values_are_unescaped()
    {
        var request = ProtocolRequest.Parse("overshell://new?profile=%7Babc%7D&cwd=W%3A%5CGithub%5COverShell&title=hello+world")!;
        Assert.Equal("{abc}", request.Query["profile"]);
        Assert.Equal("W:\\Github\\OverShell", request.Query["cwd"]);
        Assert.Equal("hello world", request.Query["title"]);
    }

    [Fact]
    public void Other_schemes_and_junk_are_not_protocol_urls()
    {
        Assert.Null(ProtocolRequest.Parse("https://example.com"));
        Assert.Null(ProtocolRequest.Parse("integrations"));
        Assert.Null(ProtocolRequest.Parse(null));
        Assert.Equal("overshell://focus/a%20b", ProtocolRequest.FocusUrl("a b"));
    }
}

public class ClaudeHooksTests
{
    [Theory]
    [InlineData("SessionStart", """{ "session_id": "s1", "hook_event_name": "SessionStart", "source": "startup" }""", AgentState.Idle)]
    [InlineData("UserPromptSubmit", """{ "session_id": "s1", "prompt": "fix the build" }""", AgentState.Working)]
    [InlineData("Stop", """{ "session_id": "s1", "stop_hook_active": false }""", AgentState.Idle)]
    [InlineData("StopFailure", """{ "session_id": "s1", "error": "rate limited" }""", AgentState.Error)]
    [InlineData("Notification", """{ "session_id": "s1", "notification_type": "permission_prompt", "message": "Claude needs your permission to use Bash" }""", AgentState.Blocked)]
    [InlineData("Notification", """{ "session_id": "s1", "notification_type": "elicitation_dialog", "message": "Pick one" }""", AgentState.Blocked)]
    [InlineData("Notification", """{ "session_id": "s1", "notification_type": "idle_prompt", "message": "Claude is waiting for your input" }""", AgentState.Idle)]
    [InlineData("SessionEnd", """{ "session_id": "s1", "reason": "other" }""", AgentState.Exited)]
    public void Events_translate(string eventName, string payload, AgentState expected)
    {
        var report = ClaudeHookTranslator.Translate("t1", eventName, Jsonc.Parse(payload, out _))!;
        Assert.Equal(expected, report.State);
        Assert.Equal("claude", report.Harness);
        Assert.Equal("s1", report.SessionId);
        Assert.Equal(ClaudeHookTranslator.Source, report.Source);
    }

    [Fact]
    public void Noise_is_ignored()
    {
        Assert.Null(ClaudeHookTranslator.Translate("t1", "Notification", Jsonc.Parse("""{ "notification_type": "auth_success" }""", out _)));
        Assert.Null(ClaudeHookTranslator.Translate("t1", "PreToolUse", Jsonc.Parse("{}", out _)));
    }

    [Fact]
    public void Install_merges_into_settings_json_and_uninstall_leaves_the_rest_alone()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-claude");
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", dir.FullName);
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, """
                {
                  "model": "opus",
                  "hooks": {
                    "Stop": [ { "hooks": [ { "type": "command", "command": "echo mine" } ] } ]
                  }
                }
                """);

            var status = IntegrationInstaller.Install("claude");
            Assert.True(status.Installed, status.Note);
            Assert.True(status.Current);
            Assert.Equal(path, status.Path);

            var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal("opus", (string)root["model"]!);
            var stop = (JsonArray)root["hooks"]!["Stop"]!;
            Assert.Equal(2, stop.Count); // the user's group first, ours appended
            Assert.Equal("echo mine", (string)stop[0]!["hooks"]![0]!["command"]!);
            var command = (string)stop[1]!["hooks"]![0]!["command"]!;
            Assert.StartsWith("cmd.exe /d /c \"if defined OVERSHELL_ENDPOINT", command);
            Assert.Contains("/v1/claude/%OVERSHELL_TAB_ID%/Stop?token=%OVERSHELL_TOKEN%", command);
            foreach (var eventName in ClaudeHookTranslator.Events)
            {
                Assert.NotNull(root["hooks"]![eventName]);
            }

            // Installing twice does not duplicate.
            IntegrationInstaller.Install("claude");
            root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(2, ((JsonArray)root["hooks"]!["Stop"]!).Count);

            var removed = IntegrationInstaller.Uninstall("claude");
            Assert.False(removed.Installed);
            root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal("opus", (string)root["model"]!);
            Assert.Single((JsonArray)root["hooks"]!["Stop"]!);
            Assert.Null(root["hooks"]!["Notification"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_settings_file_with_comments_is_refused_not_rewritten()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-claude");
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", dir.FullName);
            var path = Path.Combine(dir.FullName, "settings.json");
            var original = "{\n  // my comment\n  \"model\": \"opus\"\n}\n";
            File.WriteAllText(path, original);

            var status = IntegrationInstaller.Install("claude");
            Assert.False(status.Installed);
            Assert.Contains("comments", status.Note);
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Install_creates_the_file_when_there_is_none()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-claude");
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", dir.FullName);
            var status = IntegrationInstaller.Install("claude");
            Assert.True(status.Installed && status.Current);
            Assert.True(File.Exists(status.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
            dir.Delete(recursive: true);
        }
    }
}

public class CodexIntegrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Turn_complete_payload_translates_to_an_advisory_idle_with_thread_and_summary()
    {
        var payload = Jsonc.Parse("""
            { "type": "agent-turn-complete", "thread-id": "thr_1", "turn-id": "turn_9", "cwd": "W:\\x",
              "input-messages": ["fix the build"], "last-assistant-message": "Done. Two tests were failing because of a stale cache." }
            """, out _);
        var report = CodexNotifyTranslator.Translate("t1", payload)!;
        Assert.True(report.Advisory);
        Assert.Equal(AgentState.Idle, report.State);
        Assert.Equal("codex", report.Harness);
        Assert.Equal("thr_1", report.SessionId);
        Assert.Equal("codex resume thr_1", report.ResumeCommand);
        Assert.StartsWith("Done. Two tests", report.Summary);
        Assert.Null(CodexNotifyTranslator.Translate("t1", Jsonc.Parse("""{ "type": "something-else" }""", out _)));
        Assert.Null(CodexNotifyTranslator.Translate("t1", null));
    }

    [Fact]
    public void Wire_reports_can_be_advisory_too()
    {
        var report = IntegrationReport.Parse("", Jsonc.Parse("""{ "tab": "t", "source": "script", "state": "done", "advisory": true }""", out _), out _)!;
        Assert.True(report.Advisory);
        Assert.False(IntegrationReport.Parse("", Jsonc.Parse("""{ "tab": "t", "source": "script", "state": "done" }""", out _), out _)!.Advisory);
    }

    [Fact]
    public void An_advisory_marks_the_turn_without_taking_authority()
    {
        var rules = AgentRules.LoadDefaults();
        var m = new AgentStateMachine(rules.Find("codex")!, isAgent: true);
        m.SetViewed(false, T0);

        // The detector saw output: working.
        for (var t = 0.0; t <= 1.0; t += 0.1) { m.OnOutput(T0.AddSeconds(t)); }
        Assert.Equal(AgentState.Working, m.State);

        m.OnAdvisory("codex-notify", AgentState.Idle, "turn finished", "All green.", "thr_1", T0.AddSeconds(1.2));
        Assert.Equal(AgentState.Done, m.State);
        Assert.True(m.Unread);
        Assert.Equal(AgentAuthority.Detector, m.Authority);
        Assert.Equal("thr_1", m.SessionId);
        Assert.Equal("All green.", m.Summary);

        // The quiet timer must not undo a fresh Done, and the detector keeps deciding afterwards.
        m.Tick(T0.AddSeconds(5));
        Assert.Equal(AgentState.Done, m.State);
        m.SetViewed(true, T0.AddSeconds(6));
        Assert.Equal(AgentState.Idle, m.State);
        for (var t = 7.0; t <= 8.0; t += 0.1) { m.OnOutput(T0.AddSeconds(t)); }
        Assert.Equal(AgentState.Working, m.State);
    }

    [Fact]
    public void An_advisory_is_ignored_while_a_real_integration_holds_authority()
    {
        var m = new AgentStateMachine(AgentRules.LoadDefaults().Find("codex")!, isAgent: true);
        m.SetViewed(true, T0);
        m.OnReport("plugin", 1, AgentState.Working, null, null, null, T0);
        m.OnAdvisory("codex-notify", AgentState.Idle, null, null, "thr_2", T0.AddSeconds(1));
        Assert.Equal(AgentState.Working, m.State);
        Assert.Null(m.SessionId);
    }

    [Fact]
    public void Install_adds_a_marked_notify_line_before_the_first_table_and_uninstall_removes_only_that()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-codex");
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", dir.FullName);
            var config = Path.Combine(dir.FullName, "config.toml");
            var original = "model = \"o3\"\napproval_policy = \"on-request\"\n\n[tui]\nnotifications = true\n";
            File.WriteAllText(config, original);

            var status = IntegrationInstaller.Install("codex");
            Assert.True(status.Installed, status.Note);
            Assert.True(status.Current);
            Assert.True(File.Exists(IntegrationInstaller.CodexScriptPath()));

            var text = File.ReadAllText(config);
            var lines = text.Split('\n');
            var notify = Array.FindIndex(lines, l => l.StartsWith("notify = [", StringComparison.Ordinal));
            var table = Array.FindIndex(lines, l => l.StartsWith("[tui]", StringComparison.Ordinal));
            Assert.True(notify >= 0 && notify < table, "notify must sit among the top-level keys");
            Assert.Contains("powershell.exe", lines[notify]);
            Assert.Contains("overshell-notify.ps1", lines[notify]);
            Assert.StartsWith("model = ", lines[0]);
            Assert.Contains("[tui]\nnotifications = true", text);

            // Installing again does not double up.
            IntegrationInstaller.Install("codex");
            Assert.Single(File.ReadAllText(config).Split('\n').Where(l => l.StartsWith("notify = [", StringComparison.Ordinal)));

            var removed = IntegrationInstaller.Uninstall("codex");
            Assert.False(removed.Installed);
            Assert.Equal(original.TrimEnd('\n'), File.ReadAllText(config).TrimEnd('\n'));
            Assert.False(File.Exists(IntegrationInstaller.CodexScriptPath()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_notify_the_user_wrote_is_never_replaced()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-codex");
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", dir.FullName);
            var config = Path.Combine(dir.FullName, "config.toml");
            var original = "notify = [\"python3\", \"/home/me/notify.py\"]\n";
            File.WriteAllText(config, original);

            var status = IntegrationInstaller.Install("codex");
            Assert.False(status.Installed);
            Assert.Contains("notify command of yours", status.Note);
            Assert.Equal(original, File.ReadAllText(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Install_creates_config_toml_when_there_is_none()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-codex");
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", dir.FullName);
            var status = IntegrationInstaller.Install("codex");
            Assert.True(status.Installed && status.Current, status.Note);
            var text = File.ReadAllText(status.Path);
            Assert.StartsWith("# OverShell integration v1", text);
            Assert.Contains("\nnotify = [", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
            dir.Delete(recursive: true);
        }
    }
}