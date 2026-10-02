namespace OverShell.Core.Settings;

/// <summary>A rectangle in WPF device-independent units, kept WPF-free so the clamp is unit-testable.</summary>
public readonly record struct Bounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

/// <summary>
/// Puts a remembered window back on the desktop that exists <em>now</em>. Monitors come and
/// go between runs — a laptop undocked from its screen, a remote session with a smaller
/// desktop — and a window restored to coordinates nobody can see is as good as lost.
/// </summary>
public static class WindowPlacement
{
    /// <summary>How much of a window must stay on the desktop to count as reachable: enough to grab its caption.</summary>
    public const double MinVisibleWidth = 120;

    public const double MinVisibleHeight = 60;

    /// <summary>
    /// The saved bounds when at least a caption's worth of them lies inside
    /// <paramref name="desktop"/> (the virtual screen); otherwise the same size moved to
    /// the desktop's top-left, shrunk to fit when it is larger than the desktop; null when
    /// the saved size is unusable, which tells the caller to keep its defaults.
    /// </summary>
    public static Bounds? Clamp(SavedWindow? saved, Bounds desktop, double minWidth, double minHeight)
    {
        if (saved is null || double.IsNaN(saved.Width) || double.IsNaN(saved.Height) || saved.Width < minWidth || saved.Height < minHeight
            || double.IsNaN(saved.Left) || double.IsNaN(saved.Top) || double.IsInfinity(saved.Left) || double.IsInfinity(saved.Top))
        {
            return null;
        }

        var width = Math.Min(saved.Width, desktop.Width);
        var height = Math.Min(saved.Height, desktop.Height);
        var left = saved.Left;
        var top = saved.Top;

        var visibleWidth = Math.Min(left + width, desktop.Right) - Math.Max(left, desktop.Left);
        var visibleHeight = Math.Min(top + height, desktop.Bottom) - Math.Max(top, desktop.Top);
        if (visibleWidth < MinVisibleWidth || visibleHeight < MinVisibleHeight)
        {
            left = desktop.Left;
            top = desktop.Top;
        }

        return new Bounds(left, top, width, height);
    }
}
