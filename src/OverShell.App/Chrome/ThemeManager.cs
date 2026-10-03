using System.Windows.Media;
using OverShell.Core.Settings;

namespace OverShell.App.Chrome;

/// <summary>
/// Decides and applies the colour theme (DESIGN.md §12.14): <c>settings.theme</c> is
/// <c>system</c> (follow Windows), <c>dark</c> or <c>light</c>; <c>settings.accent</c> is
/// <c>system</c> (the Windows accent in the variant that reads on the theme), <c>palette</c>
/// (the theme's own blue) or a <c>#RRGGBB</c> colour. The result goes under any skin
/// through <see cref="SkinLoader.SetBase"/>, so a skin still wins and a change repaints live.
/// High contrast is left to the dark palette with the system accent: the terminal body is
/// the user's own scheme anyway, and inventing a contrast palette here would second-guess
/// the one Windows applies to everything else.
/// </summary>
internal static class ThemeManager
{
    private static IReadOnlyDictionary<string, Color>? _light;

    /// <summary>Last applied theme name (<c>dark</c>/<c>light</c>) and the accent in use, for traces and the self-test.</summary>
    public static (string Theme, Color Accent, string AccentSource) Current { get; private set; } = ("dark", Color.FromRgb(0x4C, 0x8D, 0xFF), "palette");

    /// <summary>Applies the theme the settings and Windows call for. Returns a one-line description for the trace.</summary>
    public static string Apply(AppSettings settings)
    {
        var light = settings.Theme.ToLowerInvariant() switch
        {
            "light" => true,
            "dark" => false,
            _ => SystemTheme.AppsUseLightTheme(),
        };

        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        if (light)
        {
            _light ??= SkinLoader.LoadPalette("Theme/Palette.Light.xaml");
            foreach (var (key, color) in _light)
            {
                colors[key] = color;
            }
        }

        var accentSource = "palette";
        Color? accent = null;
        var wanted = settings.Accent?.Trim() ?? "system";
        if (wanted.StartsWith('#'))
        {
            try
            {
                accent = (Color)ColorConverter.ConvertFromString(wanted);
                accentSource = wanted;
            }
            catch (FormatException)
            {
                accentSource = $"palette ('{wanted}' is not a colour)";
            }
        }
        else if (!wanted.Equals("palette", StringComparison.OrdinalIgnoreCase))
        {
            accent = SystemTheme.Accent(onLight: light);
            accentSource = accent is null ? "palette (no system accent readable)" : "system";
        }

        if (accent is { } a)
        {
            colors["Accent.Base"] = a;
            colors["State.Working"] = a;
            colors["Accent.Muted"] = light ? Lighten(a, 0.55) : Darken(a, 0.35);
            colors["Accent.OnAccent"] = Luminance(a) > 0.45 ? Color.FromRgb(0x0B, 0x10, 0x20) : Colors.White;
        }

        var themeName = light ? "light" : "dark";
        SkinLoader.SetBase(themeName, colors);
        var inUse = accent ?? (light ? _light!["Accent.Base"] : Color.FromRgb(0x4C, 0x8D, 0xFF));
        Current = (themeName, inUse, accentSource);
        return $"{themeName} (theme setting '{settings.Theme}'{(settings.Theme.Equals("system", StringComparison.OrdinalIgnoreCase) ? $", Windows apps {(SystemTheme.AppsUseLightTheme() ? "light" : "dark")}" : string.Empty)}), accent {inUse} from {accentSource}{(SystemTheme.HighContrast ? ", high contrast on" : string.Empty)}";
    }

    private static double Luminance(Color c) => ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;

    private static Color Lighten(Color c, double t) => Color.FromRgb((byte)(c.R + ((255 - c.R) * t)), (byte)(c.G + ((255 - c.G) * t)), (byte)(c.B + ((255 - c.B) * t)));

    private static Color Darken(Color c, double t) => Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));
}
