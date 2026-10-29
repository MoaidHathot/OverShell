using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverShell.App.Chrome;
using OverShell.Core.Previews;
using Xunit;

namespace OverShell.Tests;

public class PreviewDrawingTests
{
    [Fact]
    public async Task Styled_drawings_are_background_thread_safe_and_keep_coloured_blank_cells()
    {
        var frame = new TerminalPreviewFrame(4, 1, 40, 20,
        [new(0, 0, 2, 0xffffff, 0x0000ff, PreviewTextStyle.Normal, "  "),
         new(2, 0, 2, 0xffffff, 0xff0000, PreviewTextStyle.Normal, "  ")]);
        var image = await Task.Run(() => PreviewDrawing.Build(frame, "Consolas", 16, CancellationToken.None));
        Assert.True(image.IsFrozen);
        Sta.Run(() =>
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) context.DrawImage(image, new Rect(0, 0, 40, 20));
            var bitmap = new RenderTargetBitmap(40, 20, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[40 * 20 * 4];
            bitmap.CopyPixels(pixels, 40 * 4, 0);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.AsSpan((10 * 40 + 10) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.AsSpan((10 * 40 + 30) * 4, 4).ToArray());
        });
    }

    [Fact]
    public async Task Styled_text_with_combined_decorations_and_unicode_can_be_frozen_off_thread()
    {
        var frame = new TerminalPreviewFrame(10, 1, 100, 24,
            [new(0, 0, 10, 0x00ff00, 0, PreviewTextStyle.Bold | PreviewTextStyle.Italic | PreviewTextStyle.Underline | PreviewTextStyle.Strikethrough, "中文 e\u0301")]);
        var image = await Task.Run(() => PreviewDrawing.Build(frame, "Consolas", 16, CancellationToken.None));
        Assert.True(image.IsFrozen);
        Assert.Equal(100, image.Width);
        Assert.Equal(24, image.Height);
    }

    [Theory]
    [InlineData(0.37)]
    [InlineData(0.77)]
    [InlineData(1.25)]
    public async Task Scaled_styled_backgrounds_have_no_seams_between_rows_or_runs(double scale)
    {
        var runs = Enumerable.Range(0, 4).SelectMany(row => Enumerable.Range(0, 4)
            .Select(column => new TerminalPreviewRun(column, row, 1, 0xffffff, 0xff0000, PreviewTextStyle.Normal, " "))).ToArray();
        var frame = new TerminalPreviewFrame(4, 4, 40, 80, runs);
        var image = await Task.Run(() => PreviewDrawing.Build(frame, "Consolas", 16, CancellationToken.None));
        Assert.True(image.IsFrozen);
        Sta.Run(() =>
        {
            var width = (int)Math.Ceiling(frame.PixelWidth * scale);
            var height = (int)Math.Ceiling(frame.PixelHeight * scale);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                context.DrawImage(image, new Rect(0, 0, frame.PixelWidth * scale, frame.PixelHeight * scale));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            for (var y = 1; y < height - 1; y++)
                for (var x = 1; x < width - 1; x++)
                    Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.AsSpan((y * width + x) * 4, 4).ToArray());
        });
    }

    [Fact]
    public void Abandoned_drawings_are_cancellable()
    {
        var frame = new TerminalPreviewFrame(1, 1, 10, 20, [new(0, 0, 1, 0xffffff, 0, PreviewTextStyle.Normal, "x")]);
        Assert.Throws<OperationCanceledException>(() => PreviewDrawing.Build(frame, "Consolas", 16, new CancellationToken(true)));
    }
}
