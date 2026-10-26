using System.Text;
using System.Windows;
using OverShell.App.Input;
using Xunit;

namespace OverShell.Tests;

public class HtmlClipboardTests
{
    [Theory]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r", false)]
    public void Html_export_removes_the_transport_header_using_utf8_byte_offsets(string newline, bool stream) => WithStore(store =>
    {
        const string html = "<html><body>é<!--StartFragment--><b>日本語 🚀</b><!--EndFragment--></body></html>";
        var envelope = Envelope(html, newline, context: true);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(envelope));
        source.Position = source.Length;
        var data = new DataObject(DataFormats.Html, stream ? source : envelope, false);
        var path = Assert.Single(TerminalClipboard.Read(data, store, asFiles: true).Files);
        Assert.Equal(html, File.ReadAllText(path));
        Assert.True(File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(source.Length, source.Position);
    });

    [Fact]
    public void Fragment_only_html_uses_fragment_offsets() => WithStore(store =>
    {
        const string html = "<!--StartFragment--><p>日本語 🚀</p><!--EndFragment-->";
        var data = new DataObject(DataFormats.Html, Envelope(html, "\r\n", context: false));
        var path = Assert.Single(TerminalClipboard.Read(data, store, asFiles: true).Files);
        Assert.Equal("<p>日本語 🚀</p>", File.ReadAllText(path));
    });

    [Fact]
    public void Invalid_offsets_use_fragment_markers_when_available() => WithStore(store =>
    {
        var data = new DataObject(DataFormats.Html, "Version:1.0\nStartHTML:999999\nEndHTML:999999\n<html><!--StartFragment--><b>content</b><!--EndFragment--></html>");
        var path = Assert.Single(TerminalClipboard.Read(data, store, asFiles: true).Files);
        Assert.Equal("<b>content</b>", File.ReadAllText(path));
    });

    [Theory]
    [InlineData("HTML Format")]
    [InlineData("html format")]
    public void Plain_html_is_preserved_and_normal_paste_prefers_its_text_representation(string format) => WithStore(store =>
    {
        const string html = "<p>é 日本語</p>";
        var data = new DataObject(format, html);
        data.SetText("é 日本語");
        Assert.Equal("é 日本語", TerminalClipboard.Read(data, store).Text);
        var path = Assert.Single(TerminalClipboard.Read(data, store, asFiles: true).Files);
        Assert.Equal(html, File.ReadAllText(path));
    });

    [Fact]
    public void Invalid_html_envelope_is_reported_without_leaving_a_file() => WithStore(store =>
    {
        var data = new DataObject(DataFormats.Html, "Version:1.0\nStartHTML:99999\nEndHTML:100000\n<p>unbounded</p>");
        Assert.Throws<InvalidDataException>(() => TerminalClipboard.Read(data, store, asFiles: true));
        Assert.False(Directory.Exists(store.Root));
    });

    private static string Envelope(string html, string newline, bool context)
    {
        string Header(int start, int end, int fragmentStart, int fragmentEnd) =>
            $"Version:1.0{newline}StartHTML:{start:D10}{newline}EndHTML:{end:D10}{newline}StartFragment:{fragmentStart:D10}{newline}EndFragment:{fragmentEnd:D10}{newline}";
        var length = Encoding.UTF8.GetByteCount(Header(context ? 0 : -1, context ? 0 : -1, 0, 0));
        var fragment = html.IndexOf("<!--StartFragment-->", StringComparison.Ordinal) + "<!--StartFragment-->".Length;
        var endFragment = html.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);
        return Header(context ? length : -1, context ? length + Encoding.UTF8.GetByteCount(html) : -1,
            length + Encoding.UTF8.GetByteCount(html[..fragment]), length + Encoding.UTF8.GetByteCount(html[..endFragment])) + html;
    }

    private static void WithStore(Action<TransferFileStore> action) => Sta.Run(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "overshell-html-" + Guid.NewGuid().ToString("N"));
        try { action(new TransferFileStore(root)); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
}
