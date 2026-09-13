using OverShell.Core.Agents;
using Xunit;

namespace OverShell.Tests;

public class AgentRulesTests
{
    private static readonly AgentRules Rules = AgentRules.LoadDefaults();

    [Fact]
    public void Bundled_rule_files_load_cleanly()
    {
        Assert.Empty(Rules.Problems);
        Assert.Contains(Rules.All, r => r.Id == "opencode");
        Assert.Contains(Rules.All, r => r.Id == "copilot");
        Assert.Contains(Rules.All, r => r.Id == "claude");
        Assert.Contains(Rules.All, r => r.Id == "codex");
        Assert.Equal("generic", Rules.Generic.Id);
    }

    [Theory]
    [InlineData("opencode", "opencode")]
    [InlineData("C:\\Users\\me\\AppData\\Roaming\\npm\\opencode.cmd", "opencode")]
    [InlineData("\"P:\\winget\\copilot.exe\" --model gpt", "copilot")]
    [InlineData("claude --resume abc", "claude")]
    [InlineData("codex", "codex")]
    [InlineData("pwsh.exe -NoLogo", null)]
    [InlineData("C:\\tools\\opencoder.exe", null)]
    public void Detects_harness_from_commandline(string commandline, string? expected) =>
        Assert.Equal(expected, Rules.DetectFromCommandline(commandline));

    [Theory]
    [InlineData("OC | OverShell | fix the tab strip", "opencode")]
    [InlineData("❓ OC | OverShell", "opencode")]
    [InlineData("✳ Claude Code", "claude")]
    [InlineData("⠐ refactoring auth", null)]
    [InlineData("GitHub Copilot", "copilot")]
    [InlineData("PowerShell", null)]
    public void Detects_harness_from_title(string title, string? expected) =>
        Assert.Equal(expected, Rules.DetectFromTitle(title));

    [Theory]
    [InlineData("opencode", "opencode")]
    [InlineData("COPILOT", "copilot")]
    [InlineData("pwsh", null)]
    public void Detects_harness_from_process(string image, string? expected) =>
        Assert.Equal(expected, Rules.DetectFromProcess(image));

    [Fact]
    public void User_file_replaces_bundled_rule_set_wholesale()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-agents");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "claude.jsonc"), """{ "displayName": "My Claude", "idleAfterMs": 4000 }""");
            var rules = AgentRules.Load(dir.FullName);
            Assert.Empty(rules.Problems);
            var claude = rules.Find("claude")!;
            Assert.Equal("My Claude", claude.DisplayName);
            Assert.Equal(4000, claude.IdleAfterMs);
            Assert.Empty(claude.Detect.Title); // replaced, not merged
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Broken_user_file_is_reported_not_thrown()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-agents");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "broken.jsonc"), "{ this is not json");
            var rules = AgentRules.Load(dir.FullName);
            Assert.Single(rules.Problems);
            Assert.NotNull(rules.Find("claude"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Screen_rules_are_strict_about_blocked()
    {
        var claude = Rules.Find("claude")!;
        Assert.Equal(AgentState.Blocked, claude.Screen.Compiled.Match("Do you want to proceed?\n❯ 1. Yes\n  2. Yes, and don't ask again")!.Value.State);
        Assert.Equal(AgentState.Working, claude.Screen.Compiled.Match("✻ Thinking… (12s · ↓ 1.2k tokens)\n  Esc to interrupt")!.Value.State);
        Assert.Equal(AgentState.Idle, claude.Screen.Compiled.Match("> \n  ? for shortcuts")!.Value.State);
        Assert.Null(claude.Screen.Compiled.Match("Here is the plan:\n1. Refactor\n2. Test"));
    }

    [Fact]
    public void OpenCode_title_icons_map_to_states()
    {
        var oc = Rules.Find("opencode")!;
        Assert.Equal(AgentState.Blocked, oc.Title.Compiled.Match("❓ OC | repo | task")!.Value.State);
        Assert.Equal(AgentState.Error, oc.Title.Compiled.Match("❌ OC | repo")!.Value.State);
        Assert.Equal(AgentState.Done, oc.Title.Compiled.Match("🔔 OC | repo")!.Value.State);
        Assert.Null(oc.Title.Compiled.Match("OC | repo | task"));
        Assert.Equal(AgentState.Blocked, oc.Progress["1"]);
        Assert.Equal(AgentState.Working, oc.Progress["3"]);
        Assert.Equal(AgentState.Done, oc.Progress["4"]);
    }
}
