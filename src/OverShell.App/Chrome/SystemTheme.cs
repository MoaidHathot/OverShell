using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace OverShell.App.Chrome;

/// <summary>
/// What Windows says about colours (DESIGN.md §12.14): light or dark apps, the accent in
/// the variant that keeps contrast on each, high contrast. Read from the registry the shell
/// writes - the same values the Settings app shows - because WPF's <c>SystemParameters</c>
/// knows nothing about the immersive theme. Change notifications come through
/// <c>WM_SETTINGCHANGE</c> ("ImmersiveColorSet") and <c>WM_DWMCOLORIZATIONCOLORCHANGED</c>,
/// which the main window already hooks for the backdrop.
/// </summary>
internal static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    /// <summary>True when apps are set to the light theme (the Windows default when the value is absent).</summary>
    public static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static bool HighContrast => SystemParameters.HighContrast;

    /// <summary>
    /// The accent, in the variant Windows itself uses for text and highlights on the given
    /// background: AccentLight2 on dark, AccentDark1 on light (the shell's AccentPalette,
    /// eight RGB entries from light3 to dark3); when the palette is missing, the DWM accent
    /// (ABGR) lightened or darkened by a fifth. Null when neither is readable.
    /// </summary>
    public static Color? Accent(bool onLight)
    {
        try
        {
            using var accent = Registry.CurrentUser.OpenSubKey(AccentKey);
            if (accent?.GetValue("AccentPalette") is byte[] { Length: >= 32 } palette)
            {
                var index = onLight ? 4 : 1;
                return Color.FromRgb(palette[index * 4], palette[(index * 4) + 1], palette[(index * 4) + 2]);
            }

            using var dwm = Registry.CurrentUser.OpenSubKey(DwmKey);
            if (dwm?.GetValue("AccentColor") is int abgr)
            {
                var r = (byte)(abgr & 0xFF);
                var g = (byte)((abgr >> 8) & 0xFF);
                var b = (byte)((abgr >> 16) & 0xFF);
                var baseColor = Color.FromRgb(r, g, b);
                return onLight ? Mix(baseColor, Colors.Black, 0.2) : Mix(baseColor, Colors.White, 0.35);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall through: the palette's own accent stays.
        }

        return null;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + ((b.R - a.R) * t)),
        (byte)Math.Round(a.G + ((b.G - a.G) * t)),
        (byte)Math.Round(a.B + ((b.B - a.B) * t)));
}
