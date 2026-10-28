using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OverShell.Core.Previews;

namespace OverShell.App.Chrome;

/// <summary>Styled fallback: a frozen drawing in the source renderer's pixel coordinate space.</summary>
internal static class PreviewDrawing
{
    public static DrawingImage Build(TerminalPreviewFrame frame, string fontFace, double fontPixels, CancellationToken cancellation)
    {
        var drawing = new DrawingGroup();
        var brushes = new Dictionary<uint, SolidColorBrush>();
        var typefaces = new Dictionary<PreviewTextStyle, Typeface>();
        var cellWidth = (double)frame.PixelWidth / frame.Columns;
        var cellHeight = (double)frame.PixelHeight / frame.Rows;
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
            foreach (var run in frame.Runs)
            {
                cancellation.ThrowIfCancellationRequested();
                var rect = new Rect(run.Column * cellWidth, run.Row * cellHeight, run.Cells * cellWidth, cellHeight);
                context.DrawRectangle(Brush(run.Background), null, rect);
                if (string.IsNullOrWhiteSpace(run.Text)) continue;
                if (!typefaces.TryGetValue(run.Style, out var typeface))
                {
                    typeface = new Typeface(new FontFamily(fontFace), run.Style.HasFlag(PreviewTextStyle.Italic) ? FontStyles.Italic : FontStyles.Normal,
                        run.Style.HasFlag(PreviewTextStyle.Bold) ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                    typefaces[run.Style] = typeface;
                }
                var text = new FormattedText(run.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
                    fontPixels, Brush(run.Foreground), 1.0);
                if ((run.Style & (PreviewTextStyle.Underline | PreviewTextStyle.Strikethrough)) != 0)
                {
                    var decorations = new TextDecorationCollection();
                    if (run.Style.HasFlag(PreviewTextStyle.Underline)) decorations.Add(TextDecorations.Underline[0]);
                    if (run.Style.HasFlag(PreviewTextStyle.Strikethrough)) decorations.Add(TextDecorations.Strikethrough[0]);
                    decorations.Freeze();
                    text.SetTextDecorations(decorations);
                }
                var scaleX = text.WidthIncludingTrailingWhitespace > 0 ? rect.Width / text.WidthIncludingTrailingWhitespace : 1;
                var scaleY = Math.Min(1, rect.Height / text.Height);
                context.PushClip(new RectangleGeometry(rect));
                context.PushTransform(new TranslateTransform(rect.Left, rect.Top + (rect.Height - text.Height * scaleY) / 2));
                context.PushTransform(new ScaleTransform(scaleX, scaleY));
                context.DrawText(text, new Point());
                context.Pop();
                context.Pop();
                context.Pop();
            }
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;

        SolidColorBrush Brush(uint color)
        {
            color &= 0xffffff;
            if (brushes.TryGetValue(color, out var brush)) return brush;
            brush = new SolidColorBrush(Color.FromRgb((byte)color, (byte)(color >> 8), (byte)(color >> 16)));
            brush.Freeze();
            // True-colour gradients must not grow a persistent brush cache without bound.
            if (brushes.Count < 512) brushes[color] = brush;
            return brush;
        }
    }
}
