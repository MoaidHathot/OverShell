using System.Text.Json.Nodes;
using OverShell.Core.Commands;
using OverShell.Core.Input;
using OverShell.Core.Tabs;
using Xunit;

namespace OverShell.Tests;

public class QuickAccessTests
{
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
