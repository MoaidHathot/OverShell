using OverShell.App.Input;
using OverShell.Core.Input;
using Xunit;

namespace OverShell.Tests;

public class RightClickTests
{
    [Fact]
    public void One_gesture_pastes_once_and_claims_both_native_messages()
    {
        var gesture = new RightClickGesture();
        var input = new List<string>();
        Assert.True(gesture.Press(11, () => { input.Add(TerminalPaste.Encode("one\r\ntwo\n", true)); return true; }));
        Assert.True(gesture.Release(11));
        Assert.Equal(["\u001b[200~one\rtwo\r\u001b[201~"], input);
        Assert.False(gesture.Release(11));
    }

    [Fact]
    public void Copy_does_not_turn_into_a_paste_on_release()
    {
        var gesture = new RightClickGesture();
        var selection = "selected text";
        var pasted = false;
        Assert.True(gesture.Press(12, () =>
        {
            if (selection.Length > 0) selection = string.Empty;
            else pasted = true;
            return true;
        }));
        Assert.True(gesture.Release(12));
        Assert.False(pasted);
    }

    [Fact]
    public void Foreign_or_unclaimed_release_is_not_swallowed()
    {
        var gesture = new RightClickGesture();
        Assert.False(gesture.Press(11, () => false));
        Assert.False(gesture.Release(11));
        Assert.True(gesture.Press(22, () => true));
        Assert.False(gesture.Release(11));
        Assert.False(gesture.Release(22));
    }

    [Fact]
    public void Cancellation_and_new_presses_forget_previous_ownership()
    {
        var gesture = new RightClickGesture();
        gesture.Press(11, () => true);
        gesture.Cancel();
        Assert.False(gesture.Release(11));
        gesture.Press(22, () => true);
        gesture.Press(33, () => false);
        Assert.False(gesture.Release(22));
    }

    [Theory]
    [InlineData(false, "A\t🚀\rB\rC\r[201~")]
    [InlineData(true, "\u001b[200~A\t🚀\rB\rC\r[201~\u001b[201~")]
    public void Paste_preserves_text_and_filters_embedded_control_sequences(bool bracketed, string expected) =>
        Assert.Equal(expected, TerminalPaste.Encode("A\t🚀\r\nB\nC\r\0\u0016\u001b[201~", bracketed));
}
