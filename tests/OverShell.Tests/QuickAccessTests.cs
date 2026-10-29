using System.Text.Json.Nodes;
using OverShell.Core.Commands;
using OverShell.Core.Input;
using OverShell.Core.Tabs;
using Xunit;

namespace OverShell.Tests;

public class QuickAccessTests
{
    [Fact]
    public void Aliases_are_exact_unique_runtime_addresses_independent_of_labels()
    {
        var tabs = new List<TabIdentity> { new("a", "same"), new("b", "same") };
        var addresses = new TabAddressing(() => tabs);
        Assert.Null(addresses.Resolve(new(Label: "same")).TabId);
        Assert.True(addresses.Assign("a", "Build").Success);
        Assert.Equal("a", addresses.Resolve(new(Alias: "BUILD")).TabId);
        Assert.Null(addresses.Resolve(new(Alias: "bui")).TabId);
        Assert.False(addresses.Assign("b", "build").Success);
        tabs[0] = new("a", "changed agent title");
        Assert.Equal("a", addresses.Resolve(new(Alias: "build")).TabId);
        Assert.True(addresses.Assign("a", "tests").Success);
        Assert.True(addresses.Assign("b", "build").Success);
        addresses.Forget("a");
        Assert.Null(addresses.AliasFor("a"));
        Assert.Null(new TabAddressing(() => tabs).AliasFor("b"));
        tabs.RemoveAt(1);
        Assert.False(addresses.Assign("b", "gone").Success);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("ALL")]
    [InlineData("2")]
    [InlineData("two words")]
    [InlineData("@name")]
    public void Invalid_and_reserved_aliases_are_rejected(string alias) => Assert.NotNull(TabAddressing.Validate(alias));

    [Fact]
    public void Runtime_bindings_reject_both_directions_of_prefix_conflicts_and_expire_independently()
    {
        var bindings = new RuntimeBindings();
        var map = new KeybindingMap();
        map.Apply([new("ctrl+k b", "existing")], "test");
        Assert.Null(bindings.Add("a", new("ctrl+k", "activate"), map).Registration);
        Assert.Null(bindings.Add("a", new("ctrl+k b c", "activate"), map).Registration);
        var first = bindings.Add("a", new("ctrl+k x", "activate"), map).Registration;
        Assert.NotNull(first);
        Assert.Null(bindings.Add("b", new("ctrl+k x", "activate"), map).Registration);
        Assert.True(bindings.Remove(first.Id));
        Assert.NotNull(bindings.Add("b", new("ctrl+k x", "activate"), map).Registration);
        Assert.Null(bindings.Add("a", new("ctrl+k z", "activate"), map, global: true).Registration);
    }

    [Fact]
    public void Explicit_alias_prompt_address_never_falls_back_to_a_label_prefix()
    {
        Core.Herd.AddressableTab[] tabs = [new("a", 0, "build", null, "Idle", false, true), new("b", 1, "other", null, "Idle", false, true, "build")];
        Assert.Equal("b", Core.Herd.PromptAddress.Parse("@build hello", tabs, "a").Targets.Single().Id);
        Assert.Empty(Core.Herd.PromptAddress.Parse("@alias:missing hello", tabs, "a").Targets);
        Assert.Equal("b", Core.Herd.PromptAddress.Parse("@alias:build hello", tabs, "a").Targets.Single().Id);
    }

    [Fact]
    public void Parameterized_sequences_reach_commands_with_an_isolated_argument_and_context_snapshot()
    {
        var problems = new List<string>();
        var bindings = KeybindingMap.ParseBindings(JsonNode.Parse("""
            [{"keys":"ctrl+alt+k b","command":{"action":"tab.activate","target":{"id":"b"}}}]
            """), problems, "test");
        var map = new KeybindingMap();
        map.Apply(bindings, "test");
        var dispatcher = new KeySequenceDispatcher(map, TimeSpan.FromSeconds(3));
        KeyChord.TryParse("ctrl+alt+k", out var leader);
        KeyChord.TryParse("b", out var key);
        Assert.Equal(KeyOutcome.Pending, dispatcher.Feed(leader, DateTimeOffset.Now).Outcome);
        var decision = dispatcher.Feed(key, DateTimeOffset.Now);
        var registry = new CommandRegistry { CaptureContext = () => new CommandContext("a", "owner") };
        CommandInvocation? received = null;
        registry.Register("tab.activate", "Activate", "Tabs", invocation => { received = invocation; return true; });
        Assert.True(registry.TryExecute(decision.Command!.Command, decision.Command.Arguments));
        Assert.Empty(problems);
        Assert.Equal("a", received!.Context.TabId);
        Assert.Equal("owner", received.Context.HostId);
        Assert.Equal("b", received.Arguments["target"]!["id"]!.GetValue<string>());
        received.Arguments["target"]!["id"] = "changed";
        Assert.Equal("b", decision.Command.Arguments!["target"]!["id"]!.GetValue<string>());
        Assert.False(received.Arguments.ContainsKey("action"));
    }

    [Fact]
    public void Malformed_binding_fields_are_diagnosed_instead_of_crashing_or_unbinding_a_key()
    {
        var problems = new List<string>();
        var bindings = KeybindingMap.ParseBindings(JsonNode.Parse("""
            [{"keys":3,"command":"tab.last"},{"keys":"ctrl+b","command":{"action":3}}]
            """), problems, "test");
        Assert.Empty(bindings);
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void Attention_chain_returns_to_its_origin_while_last_tab_follows_recency()
    {
        var history = new NavigationHistory();
        history.Visit("a");
        var origin = new NavigationPoint("a", "dashboard", CursorId: "b");
        history.Begin(origin);
        history.Visit("b", attention: true);
        history.Begin(new NavigationPoint("b", "terminal"));
        history.Visit("c", attention: true);
        Assert.Equal("b", history.Last("c", _ => true));
        Assert.Equal(origin, history.TakeReturn("c", _ => true));
        Assert.Null(history.Origin);
    }

    [Fact]
    public void Closed_origin_falls_back_to_a_live_visit_and_manual_navigation_ends_the_chain()
    {
        var history = new NavigationHistory();
        history.Visit("a");
        history.Begin(new NavigationPoint("a", "herd", "a", "find"));
        history.Visit("b", attention: true);
        history.Visit("c", attention: true);
        history.Forget("a");
        var fallback = history.TakeReturn("c", id => id != "a");
        Assert.Equal("b", fallback?.TabId);
        Assert.Null(fallback?.HostId);
        Assert.Null(fallback?.Focus);
        history.Begin(new NavigationPoint("b", "terminal"));
        history.Visit("d");
        Assert.Null(history.TakeReturn("d", _ => true));
    }
}
