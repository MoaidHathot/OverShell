using System.Text.Json.Nodes;
using OverShell.Core.Input;
using OverShell.Core.Tabs;
using Xunit;

namespace OverShell.Tests;

public class TabHintTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(66)]
    [InlineData(67)]
    [InlineData(1200)]
    public void Every_tab_is_uniquely_reachable_without_a_hint_being_another_hints_prefix(int count)
    {
        var ids = Enumerable.Range(0, count).Select(i => "tab-" + i).ToArray();
        var hints = TabHints.Assign(ids);
        Assert.Equal(ids, hints.Select(h => h.TabId));
        var texts = hints.Select(h => h.Text).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(count, texts.Count);
        foreach (var text in texts)
            for (var length = 1; length < text.Length; length++) Assert.DoesNotContain(text[..length], texts);
        Assert.Equal(Enumerable.Range(1, Math.Min(count, 9)).Select(i => i.ToString()), hints.Take(9).Select(h => h.Text));
        Assert.All(texts, text => Assert.DoesNotContain(text, c => c is 'I' or 'O'));
    }

    [Fact]
    public void Overflow_does_not_change_familiar_single_key_hints()
    {
        var ids = Enumerable.Range(0, 1000).Select(i => i.ToString()).ToArray();
        Assert.Equal(TabHints.Assign(ids[..33]), TabHints.Assign(ids).Take(33));
        Assert.Equal("A", TabHints.Assign(ids)[9].Text);
        Assert.Equal("01", TabHints.Assign(ids[..34])[^1].Text);
    }

    [Fact]
    public void Mode_captures_arguments_and_closes_before_dispatching_the_selected_command()
    {
        var now = DateTimeOffset.UtcNow;
        var target = new JsonObject { ["id"] = "original" };
        using var mode = new KeyMode([new("0 a", "tab.activate", Arguments: new JsonObject { ["target"] = target })], TimeSpan.FromSeconds(10), now);
        target["id"] = "different";
        var closed = 0;
        mode.Closed += () => closed++;
        Assert.Equal(KeyOutcome.Pending, mode.Feed(Key("0"), now).Outcome);
        Assert.True(mode.IsActive);
        var decision = mode.Feed(Key("a"), now.AddSeconds(1));
        Assert.Equal(KeyOutcome.Command, decision.Outcome);
        Assert.Equal("original", decision.Command!.Arguments!["target"]!["id"]!.GetValue<string>());
        Assert.False(mode.IsActive);
        Assert.Equal(1, closed);
        mode.Dispose();
        Assert.Equal(1, closed);
    }

    [Theory]
    [InlineData("esc", KeyOutcome.Cancelled)]
    [InlineData("shift+esc", KeyOutcome.Cancelled)]
    [InlineData("x", KeyOutcome.Rejected)]
    [InlineData("ctrl+shift+w", KeyOutcome.Rejected)]
    public void Cancellation_and_unknown_input_are_consumed_instead_of_reaching_a_terminal(string key, KeyOutcome expected)
    {
        var now = DateTimeOffset.UtcNow;
        using var mode = new KeyMode([new("1", "choose")], TimeSpan.FromSeconds(10), now);
        Assert.Equal(expected, mode.Feed(Key(key), now).Outcome);
        Assert.False(mode.IsActive);
        Assert.Equal(KeyOutcome.Unbound, mode.Feed(Key("a"), now).Outcome);
    }

    [Fact]
    public void A_mode_times_out_even_before_the_first_hint_key_and_prefixes_extend_the_timeout()
    {
        var now = DateTimeOffset.UtcNow;
        using var first = new KeyMode([new("0 a", "choose")], TimeSpan.FromSeconds(10), now);
        Assert.True(first.Expire(now.AddSeconds(11)));
        using var second = new KeyMode([new("0 a", "choose")], TimeSpan.FromSeconds(10), now);
        second.Feed(Key("0"), now.AddSeconds(8));
        Assert.False(second.Expire(now.AddSeconds(11)));
        Assert.Equal(KeyOutcome.Cancelled, second.Feed(Key("a"), now.AddSeconds(19)).Outcome);
    }

    [Fact]
    public void A_mode_rejects_unreachable_prefix_bindings_before_it_can_capture_input() =>
        Assert.Throws<ArgumentException>(() => new KeyMode([new("a", "one"), new("a b", "two")], TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow));

    [Fact]
    public void Badge_layers_are_owned_runtime_state_and_reveal_previous_contributions_on_disposal()
    {
        var live = new HashSet<string> { "one", "two" };
        var badges = new TabBadges(live.Contains);
        var changes = new List<string>();
        badges.Changed += (id, _) => changes.Add(id);
        using var original = badges.CreateLayer("first");
        original.Replace(new Dictionary<string, TabBadge> { ["one"] = new("1"), ["missing"] = new("X") });
        Assert.Null(badges.For("missing"));
        var overlay = badges.CreateLayer("picker");
        overlay.Replace(new Dictionary<string, TabBadge> { ["one"] = new("A", Emphasized: true), ["two"] = new("B") });
        Assert.Equal("A", badges.For("one")!.Text);
        overlay.Dispose();
        Assert.Equal("1", badges.For("one")!.Text);
        Assert.Null(badges.For("two"));
        var before = changes.Count;
        original.Replace(new Dictionary<string, TabBadge> { ["one"] = new("1") });
        Assert.Equal(before, changes.Count);
        live.Remove("one");
        badges.Forget("one");
        Assert.Null(badges.For("one"));
        original.Replace(new Dictionary<string, TabBadge> { ["one"] = new("2") });
        Assert.Null(badges.For("one"));
    }

    [Fact]
    public void Live_extension_defaults_stay_beneath_user_overrides_and_unbinding()
    {
        var layers = new KeyBindingLayers();
        using var extension = layers.Create("tabs.hints");
        extension.Replace([new("ctrl+shift+q", "tab.hints.pick"), new("alt+a", "tab.hints.activate")]);
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """[{"keys":"ctrl+shift+q","command":"unbound"},{"keys":"alt+a","command":"mine"}]""");
            var map = KeybindingMap.Load(file, layers.Bindings);
            Assert.False(map.TryResolve(Key("ctrl+shift+q"), out _));
            Assert.True(map.TryResolve(Key("alt+a"), out var command));
            Assert.Equal("mine", command);
            extension.Replace([new("ctrl+shift+f10", "tab.hints.pick")]);
            map = KeybindingMap.Load(file, layers.Bindings);
            Assert.Equal("Ctrl+Shift+F10", map.FirstChordFor("tab.hints.pick"));
            extension.Dispose();
            Assert.Null(KeybindingMap.Load(file, layers.Bindings).FirstChordFor("tab.hints.pick"));
        }
        finally { File.Delete(file); }
    }

    private static KeyChord Key(string text) { Assert.True(KeyChord.TryParse(text, out var key)); return key; }
}
