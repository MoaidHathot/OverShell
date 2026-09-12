using System.Globalization;
using System.Windows.Media;
using Microsoft.Terminal.Wpf;
using OverShell.Config;

namespace OverShell.App;

/// <summary>
/// Translates a Windows Terminal <see cref="ColorScheme"/> into the
/// <see cref="TerminalTheme"/> struct the native control expects.
/// </summary>
/// <remarks>
/// The native side wants Win32 COLORREF (0x00BBGGRR), which is byte-reversed
/// relative to the #RRGGBB strings in settings.json.
/// </remarks>
public static class TerminalThemeMapper
{
    public static TerminalTheme ToTerminalTheme(this ColorScheme scheme, TerminalCursorShape? cursorShape)
    {
        var palette = new uint[16];
        for (var i = 0; i < palette.Length; i++)
        {
            palette[i] = ToColorRef(i < scheme.Palette.Count ? scheme.Palette[i] : "#000000");
        }

        return new TerminalTheme
        {
            DefaultBackground = ToColorRef(scheme.Background),
            DefaultForeground = ToColorRef(scheme.Foreground),
            DefaultSelectionBackground = ToColorRef(scheme.SelectionBackground ?? "#FFFFFF"),
            CursorStyle = ToCursorStyle(cursorShape),
            ColorTable = palette,
        };
    }

    public static Color ToMediaColor(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return Color.FromRgb(r, g, b);
    }

    public static SolidColorBrush ToBrush(string hex)
    {
        var brush = new SolidColorBrush(ToMediaColor(hex));
        brush.Freeze();
        return brush;
    }

    private static uint ToColorRef(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return (uint)(r | (g << 8) | (b << 16));   // COLORREF = 0x00BBGGRR
    }

    private static (byte R, byte G, byte B) ParseHex(string hex)
    {
        var text = hex.AsSpan().TrimStart('#');

        // #RGB shorthand
        if (text.Length == 3)
        {
            var r3 = Nibble(text[0]);
            var g3 = Nibble(text[1]);
            var b3 = Nibble(text[2]);
            return ((byte)(r3 * 17), (byte)(g3 * 17), (byte)(b3 * 17));
        }

        if (text.Length < 6 ||
            !byte.TryParse(text[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(text.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(text.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return (0, 0, 0);
        }

        return (r, g, b);
    }

    private static int Nibble(char c) => Convert.ToInt32(c.ToString(), 16);

    private static CursorStyle ToCursorStyle(TerminalCursorShape? shape) => shape switch
    {
        TerminalCursorShape.Bar => CursorStyle.BlinkingBar,
        TerminalCursorShape.Vintage => CursorStyle.BlinkingUnderline,
        TerminalCursorShape.Underscore => CursorStyle.BlinkingUnderline,
        TerminalCursorShape.FilledBox => CursorStyle.BlinkingBlock,
        TerminalCursorShape.EmptyBox => CursorStyle.SteadyBlock,
        TerminalCursorShape.DoubleUnderscore => CursorStyle.SteadyUnderline,
        _ => CursorStyle.BlinkingBar,
    };
}
