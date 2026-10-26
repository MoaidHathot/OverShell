using System.Windows;

namespace OverShell.App.Input;

/// <summary>Routes one clipboard data object without changing the system clipboard.</summary>
internal static class TerminalClipboard
{
    public static void Paste(IDataObject data, Action<string> pasteText, Action<string> sendKey)
    {
        if (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG"))
        {
            // Applications can read image data themselves. Send a key, not a text paste:
            // TerminalTab.Paste deliberately strips this control character.
            sendKey("\u0016");
        }
        else if (data.GetData(DataFormats.UnicodeText) is string text)
        {
            pasteText(text);
        }
    }
}
