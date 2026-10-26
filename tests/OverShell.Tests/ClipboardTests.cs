using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverShell.App.Input;
using Xunit;

namespace OverShell.Tests;

public class ClipboardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bitmap_paste_reaches_application_once_even_with_text(bool withText) => Sta.Run(() =>
    {
        var data = new DataObject();
        data.SetImage(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4));
        if (withText) data.SetText("image description");
        var text = new List<string>();
        var keys = new List<string>();

        TerminalClipboard.Paste(data, text.Add, keys.Add);

        Assert.Empty(text);
        Assert.Equal(["\u0016"], keys);
    });

    [Fact]
    public void Registered_png_without_a_bitmap_uses_application_paste() => Sta.Run(() =>
    {
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream([137, 80, 78, 71]), autoConvert: false);
        var keys = new List<string>();
        TerminalClipboard.Paste(data, _ => Assert.Fail("PNG must not become text"), keys.Add);
        Assert.Equal(["\u0016"], keys);
    });

    [Fact]
    public void Unicode_multiline_text_uses_the_text_paste_path() => Sta.Run(() =>
    {
        const string source = "日本語 🚀\r\nline two\tend";
        var data = new DataObject();
        data.SetText(source);
        var text = new List<string>();
        TerminalClipboard.Paste(data, text.Add, _ => Assert.Fail("Text must use bracketed paste"));
        Assert.Equal([source], text);
    });

    [Fact]
    public void Empty_clipboard_sends_nothing() => Sta.Run(() =>
        TerminalClipboard.Paste(new DataObject(), _ => Assert.Fail("Unexpected text"), _ => Assert.Fail("Unexpected key")));
}
