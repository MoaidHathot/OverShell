using System.Buffers.Binary;
using System.Text;

namespace OverShell.Core.Previews;

[Flags]
public enum PreviewTextStyle { Normal = 0, Bold = 1, Italic = 2, Underline = 4, Strikethrough = 8 }

/// <summary>One colour/style run positioned in terminal cells. Cells and UTF-16 length differ for wide/combining glyphs.</summary>
public sealed record TerminalPreviewRun(int Column, int Row, int Cells, uint Foreground, uint Background, PreviewTextStyle Style, string Text);

/// <summary>A coherent full viewport, including coloured blank cells, captured without resizing the terminal.</summary>
public sealed record TerminalPreviewFrame(int Columns, int Rows, int PixelWidth, int PixelHeight, IReadOnlyList<TerminalPreviewRun> Runs)
{
    public const int MaximumBytes = 16 * 1024 * 1024;

    /// <summary>Decode the versioned native snapshot ABI, validating all offsets before reading text.</summary>
    public static TerminalPreviewFrame? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 32 || bytes.Length > MaximumBytes) return null;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var columns = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var rows = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        var chars = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        var pixelWidth = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
        var pixelHeight = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        var cells = (ulong)columns * rows;
        var textStart = 32UL + 32UL * count;
        if (size != bytes.Length || version != 1 || columns == 0 || rows == 0 || cells > 262144 || count > cells || chars > 1048576 || textStart + 2UL * chars != size ||
            pixelWidth == 0 || pixelHeight == 0 || pixelWidth > 65535 || pixelHeight > 65535) return null;
        var runs = new List<TerminalPreviewRun>((int)count);
        var previousRow = -1;
        var previousEnd = 0;
        for (var i = 0; i < count; i++)
        {
            var run = bytes.Slice(32 + i * 32, 32);
            var x = BinaryPrimitives.ReadUInt32LittleEndian(run);
            var y = BinaryPrimitives.ReadUInt32LittleEndian(run[4..]);
            var width = BinaryPrimitives.ReadUInt32LittleEndian(run[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(run[24..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(run[28..]);
            if (x >= columns || y >= rows || width == 0 || width > columns - x || (ulong)offset + length > chars || y < previousRow || (y == previousRow && x < previousEnd)) return null;
            var text = Encoding.Unicode.GetString(bytes.Slice((int)(textStart + 2UL * offset), (int)length * 2));
            runs.Add(new((int)x, (int)y, (int)width, BinaryPrimitives.ReadUInt32LittleEndian(run[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(run[16..]), (PreviewTextStyle)(BinaryPrimitives.ReadUInt32LittleEndian(run[20..]) & 15), text));
            previousRow = (int)y;
            previousEnd = (int)(x + width);
        }
        return new((int)columns, (int)rows, (int)pixelWidth, (int)pixelHeight, runs);
    }
}

public readonly record struct PreviewFit(double Scale, double Left, double Top, double Width, double Height)
{
    public static PreviewFit Calculate(double sourceWidth, double sourceHeight, double width, double height)
    {
        if (!double.IsFinite(sourceWidth) || !double.IsFinite(sourceHeight) || !double.IsFinite(width) || !double.IsFinite(height) ||
            sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0) return default;
        var scale = Math.Min(width / sourceWidth, height / sourceHeight);
        var fittedWidth = sourceWidth * scale;
        var fittedHeight = sourceHeight * scale;
        return new(scale, (width - fittedWidth) / 2, (height - fittedHeight) / 2, fittedWidth, fittedHeight);
    }
}
