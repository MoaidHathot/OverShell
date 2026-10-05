using OverShell.Core.Input;
using Xunit;

namespace OverShell.Tests;

public class KeySequenceTests
{
    [Theory]
    [InlineData("ctrl+shift+k j", 2, "Ctrl+Shift+K J")]
    [InlineData("ctrl+shift+p", 1, "Ctrl+Shift+P")]
    [InlineData("  ctrl+shift+k   1 ", 2, "Ctrl+Shift+K 1")]
    [InlineData("ctrl+shift+k shift+j", 2, "Ctrl+Shift+K Shift+J")]
    public void Parses_sequences_of_chords(string text, int length, string display)
    {
        Assert.True(KeySequence.TryParse(text, out var sequence));
        Assert.Equal(length, sequence.Length);
        Assert.Equal(display, sequence.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+shift+k bogus")]
    [InlineData("ctrl+ j")]
    public void Rejects_sequences_with_a_bad_chord(string text) => Assert.False(KeySequence.TryParse(text, out _));

    [Fact]
    public void Sequences_are_value_equal_and_prefix_aware()
    {
        Assert.True(KeySequence.TryParse("ctrl+shift+k j", out var a));
        Assert.True(KeySequence.TryParse("Ctrl+Shift+K J", out var b));
        Assert.True(KeySequence.TryParse("ctrl+shift+k", out var leader));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a.StartsWith(leader.Chords));
        Assert.False(leader.StartsWith(a.Chords));
        Assert.False(a.StartsWith(a.Chords));
    }

    [Fact]
    public void Map_resolves_commands_prefixes_and_nothing()
    {
        var map = new KeybindingMap();
        map.Apply(
        [
            new Keybinding("ctrl+shift+k j", "tab.next", Stay: true),
            new Keybinding("ctrl+shift+k n", "tab.new"),
            new Keybinding("ctrl+shift+p", "palette.commands"),
        ], "test");

        Chord("ctrl+shift+k", out var leader);
        Chord("j", out var j);
        Chord("p", out var p);
        Chord("ctrl+shift+p", out var palette);

        Assert.Equal(KeyResolution.Prefix, map.Resolve([leader], out _));
        Assert.Equal(KeyResolution.Command, map.Resolve([leader, j], out var bound));
        Assert.Equal("tab.next", bound!.Command);
        Assert.True(bound.Stay);
        Assert.Equal(KeyResolution.None, map.Resolve([leader, p], out _));
        Assert.Equal(KeyResolution.Command, map.Resolve([palette], out var single));
        Assert.False(single!.Stay);
        Assert.Equal(2, map.Continuations([leader]).Count);
        Assert.Equal("Ctrl+Shift+K J", map.FirstChordFor("tab.next"));
        Assert.Empty(map.Problems);
    }

    [Fact]
    public void Stay_is_ignored_for_a_single_chord()
    {
        var map = new KeybindingMap();
        map.Apply([new Keybinding("ctrl+shift+p", "palette.commands", Stay: true)], "test");
        Chord("ctrl+shift+p", out var chord);
        Assert.Equal(KeyResolution.Command, map.Resolve([chord], out var bound));
        Assert.False(bound!.Stay);
    }

    [Fact]
    public void A_bound_chord_that_starts_a_sequence_is_reported_as_shadowing()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-seq");
        try
        {
            var file = Path.Combine(dir.FullName, "keybindings.jsonc");
            File.WriteAllText(file, """
                [
                  { "keys": "ctrl+shift+k", "command": "tab.new" },
                  { "keys": "ctrl+shift+k j", "command": "tab.next", "stay": true }
                ]
                """);
            var map = KeybindingMap.Load(file);
            Assert.Contains(map.Problems, p => p.Contains("can never fire") && p.Contains("Ctrl+Shift+K J"));
            Assert.Equal("tab.next", map.Bindings.Single(kv => kv.Key.Length == 2).Value.Command);
            Assert.True(map.Bindings.Single(kv => kv.Key.Length == 2).Value.Stay);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Extension_defaults_sit_beneath_the_user_file()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-seq");
        try
        {
            var file = Path.Combine(dir.FullName, "keybindings.jsonc");
            File.WriteAllText(file, """[ { "keys": "ctrl+shift+k n", "command": "unbound" }, { "keys": "ctrl+shift+k x", "command": "tab.close" } ]""");
            var map = KeybindingMap.Load(file, [new Keybinding("ctrl+shift+k n", "tab.new"), new Keybinding("ctrl+shift+k j", "tab.next", true)]);

            Assert.DoesNotContain(map.ChordsFor("tab.new"), s => s.ToString().StartsWith("Ctrl+Shift+K", StringComparison.Ordinal));
            Assert.Contains(map.ChordsFor("tab.next"), s => s.ToString() == "Ctrl+Shift+K J");
            Assert.Contains(map.ChordsFor("tab.close"), s => s.ToString() == "Ctrl+Shift+K X");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
    [Fact]
    public void Dispatcher_runs_sequences_swallows_strays_and_times_out()
    {
        var map = new KeybindingMap();
        map.Apply(
        [
            new Keybinding("ctrl+shift+k j", "tab.next", Stay: true),
            new Keybinding("ctrl+shift+k n", "tab.new"),
            new Keybinding("ctrl+shift+p", "palette.commands"),
        ], "test");
        var dispatcher = new KeySequenceDispatcher(map, TimeSpan.FromSeconds(3));
        var t0 = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        Chord("ctrl+shift+k", out var leader);
        Chord("j", out var j);
        Chord("n", out var n);
        Chord("q", out var q);
        Chord("esc", out var esc);
        Chord("ctrl+shift+p", out var palette);

        // A plain chord, no sequence.
        Assert.Equal(KeyOutcome.Command, dispatcher.Feed(palette, t0).Outcome);
        Assert.Equal(KeyOutcome.Unbound, dispatcher.Feed(q, t0).Outcome);

        // Leader, then a stay command twice, then a leaving command.
        Assert.Equal(KeyOutcome.Pending, dispatcher.Feed(leader, t0).Outcome);
        Assert.True(dispatcher.IsPending);
        var first = dispatcher.Feed(j, t0.AddSeconds(1));
        Assert.Equal(KeyOutcome.Command, first.Outcome);
        Assert.Equal("tab.next", first.Command!.Command);
        Assert.True(dispatcher.IsPending, "stay keeps the prefix");
        Assert.Equal(KeyOutcome.Command, dispatcher.Feed(j, t0.AddSeconds(2)).Outcome);
        var leave = dispatcher.Feed(n, t0.AddSeconds(3));
        Assert.Equal(KeyOutcome.Command, leave.Outcome);
        Assert.Equal("tab.new", leave.Command!.Command);
        Assert.False(dispatcher.IsPending);

        // A stray key inside a sequence is swallowed and ends it.
        dispatcher.Feed(leader, t0);
        Assert.Equal(KeyOutcome.Rejected, dispatcher.Feed(q, t0.AddSeconds(1)).Outcome);
        Assert.False(dispatcher.IsPending);
        Assert.Equal(KeyOutcome.Unbound, dispatcher.Feed(q, t0.AddSeconds(1)).Outcome);

        // Escape cancels.
        dispatcher.Feed(leader, t0);
        Assert.Equal(KeyOutcome.Cancelled, dispatcher.Feed(esc, t0.AddSeconds(1)).Outcome);
        Assert.False(dispatcher.IsPending);

        // Timeout: the stay prefix lapses; the next chord is judged alone.
        dispatcher.Feed(leader, t0);
        dispatcher.Feed(j, t0.AddSeconds(1));
        Assert.False(dispatcher.Expire(t0.AddSeconds(3)));
        Assert.True(dispatcher.Expire(t0.AddSeconds(5)));
        Assert.False(dispatcher.IsPending);
        dispatcher.Feed(leader, t0);
        Assert.Equal(KeyOutcome.Unbound, dispatcher.Feed(q, t0.AddSeconds(10)).Outcome);
    }

    private static void Chord(string text, out KeyChord chord) => Assert.True(KeyChord.TryParse(text, out chord));
}
