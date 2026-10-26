using System.Windows.Controls;
using System.Windows.Input;
using OverShell.App;
using Xunit;

namespace OverShell.Tests;

public class TextClipboardRoutingTests
{
    [Theory]
    [InlineData("Clipboard.PASTE", true)]
    [InlineData("clipboard.copy", false)]
    [InlineData("Clipboard.CopyIfSelection", false)]
    public void Configured_clipboard_commands_reach_text_controls_independently_of_the_chord(string command, bool paste) => Sta.WpfRun(() =>
    {
        var input = new TextBox();
        var copied = 0;
        var pasted = 0;
        input.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) => { copied++; e.Handled = true; }, (_, e) => { e.CanExecute = true; e.Handled = true; }));
        input.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, (_, e) => { pasted++; e.Handled = true; }, (_, e) => { e.CanExecute = true; e.Handled = true; }));
        Assert.True(MainWindow.RouteTextClipboard(command, input));
        Assert.Equal(paste ? 1 : 0, pasted);
        Assert.Equal(paste ? 0 : 1, copied);
        return Task.CompletedTask;
    });

    [Fact]
    public void Non_editing_clipboard_commands_remain_available_with_text_focus() => Sta.WpfRun(() =>
    {
        Assert.False(MainWindow.RouteTextClipboard("clipboard.openFiles", new TextBox()));
        Assert.False(MainWindow.RouteTextClipboard("clipboard.paste", null));
        return Task.CompletedTask;
    });
}
