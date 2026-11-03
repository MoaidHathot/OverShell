using System.Windows;
using OverShell.Core.Settings;

namespace OverShell.App.Chrome;

/// <summary>Floating bounds are owner-client DIPs, so layout and DPI changes stay inside the same window.</summary>
internal static class FloatingPlacement
{
    internal static Rect Calculate(Size area, FloatingSettings settings)
    {
        if (area.Width <= 0 || area.Height <= 0) return Rect.Empty;
        var width = Math.Min(area.Width, Math.Max(360, area.Width * settings.EffectiveWidth));
        var height = Math.Min(area.Height, Math.Max(220, area.Height * settings.EffectiveHeight));
        var x = settings.Position is "topRight" or "bottomRight" ? area.Width - width - 12 : (area.Width - width) / 2;
        var y = settings.Position == "topRight" ? 12 : settings.Position == "bottomRight" ? area.Height - height - 12 : (area.Height - height) / 2;
        return Constrain(new Rect(x, y, width, height), area);
    }

    internal static Rect Constrain(Rect bounds, Size area)
    {
        if (area.Width <= 0 || area.Height <= 0) return Rect.Empty;
        var width = Math.Clamp(bounds.Width, Math.Min(200, area.Width), area.Width);
        var height = Math.Clamp(bounds.Height, Math.Min(140, area.Height), area.Height);
        return new Rect(Math.Clamp(bounds.X, 0, area.Width - width), Math.Clamp(bounds.Y, 0, area.Height - height), width, height);
    }
}
