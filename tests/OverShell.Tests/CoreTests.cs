using System.Text.Json.Nodes;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;
using OverShell.Core.Notifications;
using OverShell.Core.Search;
using OverShell.Core.Settings;
using Xunit;

namespace OverShell.Tests;

public class FuzzyMatcherTests
{
    [Fact]
    public void Requires_all_query_characters_in_order()
    {
        Assert.NotNull(FuzzyMatcher.Match("tbn", "tab.new"));
        Assert.Null(FuzzyMatcher.Match("tbz", "tab.new"));
        Assert.Null(FuzzyMatcher.Match("ntab", "tab.new"));
    }

    [Fact]
    public void Prefers_word_starts_and_consecutive_runs()
    {
        var acronym = FuzzyMatcher.Match("jta", "Jump To Attention")!.Score;
        var scattered = FuzzyMatcher.Match("jta", "adjust the axle")!.Score;
        Assert.True(acronym > scattered);

        var contiguous = FuzzyMatcher.Match("herd", "view.herd")!.Score;
        var spread = FuzzyMatcher.Match("herd", "h-e-r-d")!.Score;
        Assert.True(contiguous > spread);
    }

    [Fact]
    public void Empty_query_matches_everything_with_zero_score()
    {
        Assert.Equal(0, FuzzyMatcher.Match("", "anything")!.Score);
    }

    [Fact]
    public void Palette_query_parses_modes_and_filters()
    {
        var q = PaletteQuery.Parse("@blocked #OverShell fix tests");
        Assert.False(q.CommandsMode);
        Assert.Equal("blocked", q.StateFilter);
        Assert.Equal("OverShell", q.ProjectFilter);
        Assert.Equal("fix tests", q.Text);

        var c = PaletteQuery.Parse(">new tab");
        Assert.True(c.CommandsMode);
        Assert.Equal("new tab", c.Text);
        Assert.Null(c.StateFilter);
    }
}

public class NotificationTests
{
    private static NotificationEvent Event(bool active = false, bool focused = false, string? harness = "opencode") => new(
        NotificationKind.Blocked, "t1", "auth refactor", harness, "OpenCode", "OverShell", "W:\\Github\\OverShell",
        "Action requires your approval", null, new DateTimeOffset(2026, 9, 13, 23, 30, 0, TimeSpan.Zero), active, focused);

    [Fact]
    public void When_filters_compose()
    {
        var when = new NotificationWhen { NotActiveTab = true, Kinds = ["blocked", "error"], Harnesses = ["opencode"] };
        var noon = new TimeOnly(12, 0);

        Assert.True(when.Allows(Event(), noon));
        Assert.False(when.Allows(Event(active: true), noon));
        Assert.False(when.Allows(Event(harness: "claude"), noon));
        Assert.False(when.Allows(Event() with { Kind = NotificationKind.Done }, noon));
    }

    [Theory]
    [InlineData("22:00-07:00", "23:30", true)]
    [InlineData("22:00-07:00", "06:59", true)]
    [InlineData("22:00-07:00", "07:00", false)]
    [InlineData("22:00-07:00", "12:00", false)]
    [InlineData("09:00-17:00", "12:00", true)]
    [InlineData("09:00-17:00", "18:00", false)]
    [InlineData("garbage", "12:00", false)]
    public void Quiet_hours_wrap_midnight(string spec, string now, bool quiet) =>
        Assert.Equal(quiet, NotificationWhen.InQuietHours(spec, TimeOnly.Parse(now)));

    [Fact]
    public void Templates_expand_known_values_and_leave_unknown_visible()
    {
        var values = Event().TemplateValues();
        Assert.Equal("OpenCode · auth refactor", ArgTemplate.Expand("{title}", values));
        Assert.Equal("overshell-t1", ArgTemplate.Expand("overshell-{tab.id}", values));
        Assert.Equal("blocked", ArgTemplate.Expand("{kind}", values));
        Assert.Equal("{nope}", ArgTemplate.Expand("{nope}", values));

        var args = ArgTemplate.Expand(["-t", "{title}", "--launch", "overshell://focus/{tab.id}"], values);
        Assert.Equal(["-t", "OpenCode · auth refactor", "--launch", "overshell://focus/t1"], args);
    }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_load_and_name_the_five_sinks()
    {
        var s = AppSettings.LoadDefaults();
        Assert.Empty(s.Problems);
        Assert.Equal("terminal", s.View);
        Assert.Equal(300, s.Detection.SnapshotDebounceMs);
        Assert.Equal(["overlay", "palantir", "sound", "taskbar", "toast"], s.Notifications.Sinks.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.False(s.Notifications.Sinks["toast"].Enabled);
        Assert.False(s.Notifications.Sinks["palantir"].Enabled);
        Assert.Equal("palantir", s.Notifications.Sinks["palantir"].Exe);
        Assert.True(s.Notifications.Sinks["overlay"].When.NotActiveTab);
    }

    [Fact]
    public void User_file_merges_one_key_deep_into_a_named_sink()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-settings");
        try
        {
            var file = Path.Combine(dir.FullName, "settings.jsonc");
            File.WriteAllText(file, """
                {
                  // enable Palantir, mute sounds, switch view
                  "view": "herd",
                  "notifications": { "sinks": { "palantir": { "enabled": true }, "sound": null } },
                }
                """);

            var s = AppSettings.Load(file);
            Assert.Empty(s.Problems);
            Assert.Equal("herd", s.View);
            Assert.True(s.Notifications.Sinks["palantir"].Enabled);
            Assert.Equal("palantir", s.Notifications.Sinks["palantir"].Exe); // kept from defaults
            Assert.False(s.Notifications.Sinks.ContainsKey("sound"));         // removed by null
            Assert.True(s.Notifications.Sinks.ContainsKey("overlay"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Broken_user_file_falls_back_to_defaults_with_a_problem()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-settings");
        try
        {
            var file = Path.Combine(dir.FullName, "settings.jsonc");
            File.WriteAllText(file, "{ broken");
            var s = AppSettings.Load(file);
            Assert.Single(s.Problems);
            Assert.Equal("terminal", s.View);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Jsonc_merge_semantics()
    {
        var a = Jsonc.Parse("""{ "x": { "a": 1, "b": [1,2] }, "y": 1 }""", out _);
        var b = Jsonc.Parse("""{ "x": { "b": [3], "c": true }, "y": null }""", out _);
        var m = (JsonObject)Jsonc.Merge(a, b)!;
        Assert.Equal(1, (int)m["x"]!["a"]!);
        Assert.Equal("[3]", m["x"]!["b"]!.ToJsonString());
        Assert.True((bool)m["x"]!["c"]!);
        Assert.False(m.ContainsKey("y"));
    }
}

public class IntegrationProtocolTests
{
    [Fact]
    public void Report_parses_wire_shape()
    {
        var body = Jsonc.Parse("""{ "tab": "t1", "source": "opencode", "seq": 7, "state": "blocked", "message": "approval", "summary": "fix tests", "session": { "id": "ses_1", "resumeCommand": "opencode --session ses_1" } }""", out _);
        var r = IntegrationReport.Parse("", body, out var error)!;
        Assert.Null(error);
        Assert.Equal("t1", r.TabId);
        Assert.Equal(7, r.Seq);
        Assert.Equal(AgentState.Blocked, r.State);
        Assert.Equal("ses_1", r.SessionId);
        Assert.Equal("opencode --session ses_1", r.ResumeCommand);
        Assert.False(r.Release);
    }

    [Fact]
    public void Report_rejects_missing_fields_and_unknown_states()
    {
        Assert.Null(IntegrationReport.Parse("", Jsonc.Parse("""{ "source": "x" }""", out _), out var e1));
        Assert.Contains("required", e1);
        Assert.Null(IntegrationReport.Parse("", Jsonc.Parse("""{ "tab": "t", "source": "x", "state": "dancing" }""", out _), out var e2));
        Assert.Contains("dancing", e2);
    }

    [Theory]
    [InlineData("sessionStart", """{ "sessionId": "s1", "timestamp": 10, "cwd": "W:\\x", "source": "startup" }""", AgentState.Idle)]
    [InlineData("userPromptSubmitted", """{ "sessionId": "s1", "timestamp": 11, "prompt": "fix the build" }""", AgentState.Working)]
    [InlineData("agentStop", """{ "sessionId": "s1", "timestamp": 12, "stopReason": "end_turn" }""", AgentState.Idle)]
    [InlineData("notification", """{ "sessionId": "s1", "timestamp": 13, "notification_type": "permission_prompt", "message": "Allow rm?" }""", AgentState.Blocked)]
    [InlineData("notification", """{ "sessionId": "s1", "timestamp": 13, "notification_type": "elicitation_dialog", "message": "Which one?" }""", AgentState.Blocked)]
    [InlineData("sessionEnd", """{ "sessionId": "s1", "timestamp": 14, "reason": "user_exit" }""", AgentState.Exited)]
    [InlineData("errorOccurred", """{ "sessionId": "s1", "timestamp": 15, "error": { "message": "boom" }, "recoverable": false }""", AgentState.Error)]
    public void Copilot_events_translate(string eventName, string payload, AgentState expected)
    {
        var r = CopilotHookTranslator.Translate("t1", eventName, Jsonc.Parse(payload, out _))!;
        Assert.Equal(expected, r.State);
        Assert.Equal("copilot", r.Harness);
        Assert.Equal("s1", r.SessionId);
    }

    [Fact]
    public void Copilot_noise_is_ignored()
    {
        Assert.Null(CopilotHookTranslator.Translate("t1", "notification", Jsonc.Parse("""{ "notification_type": "shell_completed" }""", out _)));
        Assert.Null(CopilotHookTranslator.Translate("t1", "errorOccurred", Jsonc.Parse("""{ "recoverable": true, "error": { "message": "retry" } }""", out _)));
        Assert.Null(CopilotHookTranslator.Translate("t1", "preToolUse", Jsonc.Parse("{}", out _)));
    }

    [Fact]
    public void Copilot_hook_file_has_no_quotes_inside_commands_and_covers_every_event()
    {
        var text = IntegrationInstaller.CopilotHookContent();
        var root = (JsonObject)Jsonc.Parse(text, out var error)!;
        Assert.Null(error);
        var hooks = (JsonObject)root["hooks"]!;
        foreach (var ev in CopilotHookTranslator.Events)
        {
            var entry = (JsonObject)((JsonArray)hooks[ev]!)[0]!;
            Assert.Equal("cmd.exe", (string)entry["exec"]!);
            var command = (string)((JsonArray)entry["args"]!)[2]!;
            Assert.DoesNotContain('"', command);
            Assert.Contains($"/v1/copilot/%OVERSHELL_TAB_ID%/{ev}?token=%OVERSHELL_TOKEN%", command);
            Assert.StartsWith("if defined OVERSHELL_ENDPOINT", command);
        }
    }

    [Fact]
    public void OpenCode_plugin_guards_on_environment()
    {
        var plugin = IntegrationInstaller.OpenCodePluginContent();
        Assert.Contains("process.env.OVERSHELL_ENDPOINT", plugin);
        Assert.Contains("if (!endpoint || !token || !tab) return {}", plugin);
        Assert.Contains("permission.asked", plugin);
        Assert.Contains("question.asked", plugin);
        Assert.Contains("session.status", plugin);
    }

    [Fact]
    public async Task Endpoint_authenticates_and_dispatches()
    {
        using var endpoint = new IntegrationEndpoint();
        endpoint.Start();
        var received = new List<IntegrationReport>();
        endpoint.ReportReceived += received.Add;
        endpoint.TabsProvider = () => [new TabSummary("t1", "shell", null, "Idle", null, null)];

        using var http = new HttpClient();

        var unauthorized = await http.PostAsync($"{endpoint.BaseUrl}/v1/report", new StringContent("""{ "tab": "t1", "source": "x", "state": "working" }"""));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.BaseUrl}/v1/report")
        {
            Content = new StringContent("""{ "tab": "t1", "source": "test", "seq": 1, "state": "blocked", "message": "hi" }""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.Token);
        var ok = await http.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);

        // Query-string token, the shim's path, plus the Copilot translator route.
        var copilot = await http.PostAsync(
            $"{endpoint.BaseUrl}/v1/copilot/t1/agentStop?token={endpoint.Token}",
            new StringContent("""{ "sessionId": "s9", "timestamp": 1 }""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.OK, copilot.StatusCode);

        var tabsRequest = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.BaseUrl}/v1/tabs");
        tabsRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.Token);
        var tabs = await (await http.SendAsync(tabsRequest)).Content.ReadAsStringAsync();
        Assert.Contains("\"t1\"", tabs);

        // ReportReceived fires on the listener thread; give it a moment.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (received.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(2, received.Count);
        Assert.Equal(AgentState.Blocked, received[0].State);
        Assert.Equal(CopilotHookTranslator.Source, received[1].Source);
        Assert.Equal("s9", received[1].SessionId);
    }
}

public class AppPathsTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] pairs) =>
        name => pairs.FirstOrDefault(p => p.Name == name).Value;

    [Fact]
    public void Explicit_override_wins_over_everything()
    {
        var root = AppPaths.ResolveConfigRoot(Env(("OVERSHELL_CONFIG_DIR", @"W:\cfg\os"), ("XDG_CONFIG_HOME", @"P:\dotfiles")));
        Assert.Equal(@"W:\cfg\os", root.Path);
        Assert.Equal(PathSource.Override, root.Source);
    }

    [Fact]
    public void Xdg_config_home_gets_an_overshell_folder_and_is_normalised()
    {
        var root = AppPaths.ResolveConfigRoot(Env(("XDG_CONFIG_HOME", "P:\\Github\\dotfiles\\configurations/../config/")));
        Assert.Equal(@"P:\Github\dotfiles\config\overshell", root.Path);
        Assert.Equal(PathSource.Xdg, root.Source);
        Assert.Equal("XDG_CONFIG_HOME", root.Variable);
    }

    [Fact]
    public void Relative_or_empty_xdg_values_fall_through_to_the_windows_default()
    {
        var relative = AppPaths.ResolveConfigRoot(Env(("XDG_CONFIG_HOME", "dotfiles/config")));
        Assert.Equal(PathSource.Windows, relative.Source);
        Assert.EndsWith(@"\OverShell", relative.Path);

        var empty = AppPaths.ResolveConfigRoot(Env(("XDG_CONFIG_HOME", "   ")));
        Assert.Equal(PathSource.Windows, empty.Source);

        var none = AppPaths.ResolveStateRoot(Env());
        Assert.Equal(PathSource.Windows, none.Source);
        Assert.Equal("LOCALAPPDATA", none.Variable);
    }

    [Fact]
    public void State_follows_xdg_state_home_independently_of_config()
    {
        var state = AppPaths.ResolveStateRoot(Env(("XDG_CONFIG_HOME", @"P:\dotfiles"), ("XDG_STATE_HOME", @"D:\state")));
        Assert.Equal(@"D:\state\overshell", state.Path);
        Assert.Equal(PathSource.Xdg, state.Source);

        var config = AppPaths.ResolveConfigRoot(Env(("XDG_STATE_HOME", @"D:\state")));
        Assert.Equal(PathSource.Windows, config.Source);
    }
}
public class StarterFilesTests
{
    [Fact]
    public void The_embedded_defaults_load_back_unchanged_as_user_files()
    {
        // `settings init` copies the defaults into the user's directory; merging a copy of the
        // defaults over the defaults must be a no-op, and the copy must parse without problems.
        var dir = Directory.CreateTempSubdirectory("overshell-init");
        try
        {
            var settingsPath = Path.Combine(dir.FullName, "settings.jsonc");
            File.WriteAllText(settingsPath, "// header\n" + EmbeddedResources.Read("settings.jsonc"));
            var user = AppSettings.Load(settingsPath);
            var defaults = AppSettings.LoadDefaults();
            Assert.Empty(user.Problems);
            Assert.Equal(Jsonc.Serialize(defaults), Jsonc.Serialize(user));

            var keysPath = Path.Combine(dir.FullName, "keybindings.jsonc");
            File.WriteAllText(keysPath, "// header\n" + EmbeddedResources.Read("keybindings.jsonc"));
            var map = OverShell.Core.Input.KeybindingMap.Load(keysPath);
            Assert.Empty(map.Problems);
            Assert.Equal(OverShell.Core.Input.KeybindingMap.Load(null).Bindings.Count, map.Bindings.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}