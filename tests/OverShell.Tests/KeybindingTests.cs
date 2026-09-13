using OverShell.Core;
using OverShell.Core.Input;
using Xunit;

namespace OverShell.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("ctrl+shift+p", ChordModifiers.Control | ChordModifiers.Shift, "P")]
    [InlineData("CTRL+T", ChordModifiers.Control, "T")]
    [InlineData("alt+1", ChordModifiers.Alt, "D1")]
    [InlineData("ctrl+pgdn", ChordModifiers.Control, "PageDown")]
    [InlineData("ctrl+pgup", ChordModifiers.Control, "PageUp")]
    [InlineData("ctrl+tab", ChordModifiers.Control, "Tab")]
    [InlineData("shift+ctrl+tab", ChordModifiers.Control | ChordModifiers.Shift, "Tab")]
    [InlineData("ctrl+shift+space", ChordModifiers.Control | ChordModifiers.Shift, "Space")]
    [InlineData("ctrl+shift+backtick", ChordModifiers.Control | ChordModifiers.Shift, "OemTilde")]
    [InlineData("ctrl+plus", ChordModifiers.Control, "OemPlus")]
    [InlineData("ctrl+minus", ChordModifiers.Control, "OemMinus")]
    [InlineData("f5", ChordModifiers.None, "F5")]
    [InlineData("ctrl+f12", ChordModifiers.Control, "F12")]
    [InlineData("esc", ChordModifiers.None, "Escape")]
    [InlineData("win+e", ChordModifiers.Win, "E")]
    [InlineData(" ctrl + shift + j ", ChordModifiers.Control | ChordModifiers.Shift, "J")]
    public void Parses_windows_terminal_notation(string text, ChordModifiers modifiers, string key)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(modifiers, chord.Modifiers);
        Assert.Equal(key, chord.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+")]
    [InlineData("ctrl+shift")]
    [InlineData("ctrl+a+b")]
    public void Rejects_incomplete_or_double_key_chords(string text) =>
        Assert.False(KeyChord.TryParse(text, out _));

    [Theory]
    [InlineData("ctrl+shift+p", "Ctrl+Shift+P")]
    [InlineData("alt+1", "Alt+1")]
    [InlineData("ctrl+pgdn", "Ctrl+PgDn")]
    [InlineData("ctrl+shift+backtick", "Ctrl+Shift+`")]
    [InlineData("esc", "Esc")]
    public void Displays_in_user_form(string text, string display)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(display, chord.ToString());
    }

    [Fact]
    public void Chords_are_value_equal_regardless_of_input_spelling()
    {
        Assert.True(KeyChord.TryParse("Ctrl+Shift+P", out var a));
        Assert.True(KeyChord.TryParse("shift+control+p", out var b));
        Assert.Equal(a, b);
    }
}

public class KeybindingMapTests
{
    [Fact]
    public void Defaults_load_without_problems_and_cover_the_original_shortcuts()
    {
        var map = KeybindingMap.Load(null);
        Assert.Empty(map.Problems);

        Assert.Equal("tab.new", Resolve(map, "ctrl+t"));
        Assert.Equal("tab.close", Resolve(map, "ctrl+shift+w"));
        Assert.Equal("tab.next", Resolve(map, "ctrl+tab"));
        Assert.Equal("tab.previous", Resolve(map, "ctrl+shift+tab"));
        Assert.Equal("tab.switchTo.3", Resolve(map, "alt+3"));
        Assert.Equal("clipboard.copyIfSelection", Resolve(map, "ctrl+c"));
        Assert.Equal("clipboard.paste", Resolve(map, "ctrl+v"));
        Assert.Equal("clipboard.copy", Resolve(map, "ctrl+shift+c"));
        Assert.Equal("palette.commands", Resolve(map, "ctrl+shift+p"));
        Assert.Equal("tab.jumpToAttention", Resolve(map, "ctrl+shift+j"));
    }

    [Fact]
    public void User_file_overrides_and_unbinds()
    {
        var dir = Directory.CreateTempSubdirectory("overshell-kb");
        try
        {
            var file = Path.Combine(dir.FullName, "keybindings.jsonc");
            File.WriteAllText(file, """
                // user overrides
                [
                  { "keys": "ctrl+t", "command": "unbound" },
                  { "keys": "ctrl+n", "command": "tab.new" },
                  { "keys": "ctrl+shift+j", "command": "view.herd" },
                  { "keys": "bogus", "command": "tab.new" },
                ]
                """);

            var map = KeybindingMap.Load(file);

            Assert.Null(Resolve(map, "ctrl+t"));
            Assert.Equal("tab.new", Resolve(map, "ctrl+n"));
            Assert.Equal("view.herd", Resolve(map, "ctrl+shift+j"));
            Assert.Contains(map.Problems, p => p.Contains("bogus"));
            Assert.Equal("Ctrl+N", map.FirstChordFor("tab.new"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Accepts_windows_terminal_object_shape()
    {
        var node = Jsonc.Parse("""{ "keybindings": [ { "command": { "action": "tab.new" }, "keys": "ctrl+shift+t" } ] }""", out var error);
        Assert.Null(error);
        var problems = new List<string>();
        var bindings = KeybindingMap.ParseBindings(node, problems, "test");
        Assert.Empty(problems);
        Assert.Single(bindings);
        Assert.Equal("tab.new", bindings[0].Command);
    }

    private static string? Resolve(KeybindingMap map, string chordText)
    {
        Assert.True(KeyChord.TryParse(chordText, out var chord));
        return map.TryResolve(chord, out var id) ? id : null;
    }
}
