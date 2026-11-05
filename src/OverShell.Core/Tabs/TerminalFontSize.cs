namespace OverShell.Core.Tabs;

/// <summary>Terminal font sizes are points, not a scale transform on the rendered viewport.</summary>
public static class TerminalFontSize
{
    public const int Minimum = 1;
    public const int Maximum = 128;
    public const int Default = 12;

    public static bool IsValid(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum;
    public static int Normalize(double value) => (int)Math.Round(Math.Clamp(double.IsFinite(value) ? value : Default, Minimum, Maximum));
    public static int Adjust(double current, int delta) => Normalize(current + delta);
}
