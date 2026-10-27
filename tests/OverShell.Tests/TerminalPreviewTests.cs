using System.Buffers.Binary;
using System.Text;
using OverShell.Core.Previews;
using Xunit;

namespace OverShell.Tests;

public class TerminalPreviewTests
{
    private static byte[] Frame(string text = "中文😀e\u0301")
    {
        var utf16 = Encoding.Unicode.GetBytes(text);
        var data = new byte[32 + 32 + utf16.Length];
        uint[] header = [(uint)data.Length, 1, 80, 25, 1, (uint)text.Length, 800, 500];
        uint[] run = [4, 3, 7, 0x0000ff, 0xff0000, 7, 0, (uint)text.Length];
        for (var i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), header[i]);
        for (var i = 0; i < run.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32 + i * 4), run[i]);
        utf16.CopyTo(data, 64);
        return data;
    }

    [Fact]
    public void Snapshot_preserves_colours_styles_and_wide_combining_text_without_confusing_cells_with_utf16()
    {
        var frame = TerminalPreviewFrame.Decode(Frame())!;
        Assert.Equal((80, 25), (frame.Columns, frame.Rows));
        var run = Assert.Single(frame.Runs);
        Assert.Equal("中文😀e\u0301", run.Text);
        Assert.Equal(7, run.Cells);
        Assert.Equal((4, 3), (run.Column, run.Row));
        Assert.Equal(0x0000ffu, run.Foreground);
        Assert.Equal(0xff0000u, run.Background);
        Assert.Equal(PreviewTextStyle.Bold | PreviewTextStyle.Italic | PreviewTextStyle.Underline, run.Style);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 2)]
    [InlineData(8, 0)]
    [InlineData(12, 0)]
    [InlineData(16, uint.MaxValue)]
    [InlineData(20, uint.MaxValue)]
    [InlineData(24, 0)]
    [InlineData(28, uint.MaxValue)]
    [InlineData(32, 80)]
    [InlineData(36, 25)]
    [InlineData(40, 0)]
    [InlineData(40, 100)]
    [InlineData(56, uint.MaxValue)]
    [InlineData(60, uint.MaxValue)]
    public void Malformed_native_headers_and_runs_are_rejected(int offset, uint value)
    {
        var data = Frame();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        Assert.Null(TerminalPreviewFrame.Decode(data));
    }

    [Fact]
    public void Truncated_blocks_are_rejected()
    {
        var data = Frame();
        for (var length = 0; length < data.Length; length++) Assert.Null(TerminalPreviewFrame.Decode(data.AsSpan(0, length)));
    }

    [Theory]
    [InlineData(2000, 1000, 400, 300, 0.2, 0, 50)]
    [InlineData(1000, 2000, 400, 300, 0.15, 125, 0)]
    [InlineData(800, 600, 800, 600, 1, 0, 0)]
    public void Aspect_fit_preserves_the_original_shape(double sw, double sh, double w, double h, double scale, double x, double y)
    {
        var fit = PreviewFit.Calculate(sw, sh, w, h);
        Assert.Equal(scale, fit.Scale, 8);
        Assert.Equal(x, fit.Left, 8);
        Assert.Equal(y, fit.Top, 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_geometry_has_no_drawable_area(double invalid) => Assert.Equal(default, PreviewFit.Calculate(invalid, 100, 400, 300));
}
