using OverShell.Core.Input;
using OverShell.Core.Tabs;
using Xunit;

namespace OverShell.Tests;

public class TerminalFontSizeTests
{
    [Fact]
    public void Repeated_zoom_stops_at_supported_sizes_and_invalid_values_are_rejected()
    {
        Assert.Equal(TerminalFontSize.Maximum, TerminalFontSize.Adjust(128, 16));
        Assert.Equal(TerminalFontSize.Minimum, TerminalFontSize.Adjust(1, -16));
        Assert.False(TerminalFontSize.IsValid(double.NaN));
        Assert.False(TerminalFontSize.IsValid(double.PositiveInfinity));
        Assert.False(TerminalFontSize.IsValid(0));
        Assert.False(TerminalFontSize.IsValid(129));
        Assert.Equal(12, TerminalFontSize.Normalize(double.NaN));
    }

    [Theory]
    [InlineData("ctrl+=", "terminal.zoomIn")]
    [InlineData("ctrl+shift+plus", "terminal.zoomIn")]
    [InlineData("ctrl+add", "terminal.zoomIn")]
    [InlineData("ctrl+-", "terminal.zoomOut")]
    [InlineData("ctrl+subtract", "terminal.zoomOut")]
    [InlineData("ctrl+0", "terminal.zoomReset")]
    public void Font_shortcuts_are_regular_overridable_commands(string keys, string expected)
    {
        var map = KeybindingMap.Load(null);
        Assert.True(KeyChord.TryParse(keys, out var chord));
        Assert.True(map.TryResolve(chord, out var command));
        Assert.Equal(expected, command);
        map.Apply([new(keys, "unbound")], "user");
        Assert.False(map.TryResolve(chord, out _));
    }
}
