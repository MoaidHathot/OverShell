using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OverShell.App;

/// <summary>Which Windows 11 system backdrop to draw behind the window chrome.</summary>
internal enum BackdropKind
{
    None = 0,

    /// <summary>Mica — wallpaper-tinted, does not show windows behind. Microsoft's pick for main windows.</summary>
    Mica = 2,

    /// <summary>Acrylic — stronger blur that *does* show what is behind the window.</summary>
    Acrylic = 3,

    /// <summary>Mica Alt — the tabbed-window variant, slightly stronger tint than Mica.</summary>
    MicaAlt = 4,
}

/// <summary>
/// Opts into the Windows 11 window presentation we still want even though the caption
/// is drawn by us: rounded corners, dark chrome, a muted border, and a system backdrop
/// behind the translucent parts of the chrome.
/// </summary>
internal static class WindowChromeInterop
{
    private enum Attribute
    {
        UseImmersiveDarkMode = 20,
        WindowCornerPreference = 33,
        BorderColor = 34,
        SystemBackdropType = 38,
    }

    private enum CornerPreference
    {
        Round = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>
    /// Backdrop selection, overridable at launch with
    /// <c>OVERSHELL_BACKDROP=acrylic|mica|micaalt|none</c> so the look can be compared
    /// without a rebuild.
    /// </summary>
    public static BackdropKind Resolve() =>
        Environment.GetEnvironmentVariable("OVERSHELL_BACKDROP")?.Trim().ToLowerInvariant() switch
        {
            "none" => BackdropKind.None,
            "mica" => BackdropKind.Mica,
            "micaalt" => BackdropKind.MicaAlt,
            "acrylic" => BackdropKind.Acrylic,
            _ => BackdropKind.Acrylic,
        };

    /// <summary>
    /// The user can switch "Transparency effects" off in Settings; honouring it keeps us
    /// consistent with the rest of the shell.
    /// </summary>
    public static bool TransparencyEffectsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Applies chrome attributes.</summary>
    /// <param name="window">The window to decorate.</param>
    /// <param name="requested">Which backdrop to draw.</param>
    /// <param name="topBandDip">Title bar height, in DIPs.</param>
    /// <param name="bottomBandDip">Status bar height, in DIPs.</param>
    /// <returns>True when a system backdrop is actually active, so callers can pick brushes.</returns>
    public static bool Apply(Window window, BackdropKind requested, double topBandDip, double bottomBandDip)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        Set(handle, Attribute.UseImmersiveDarkMode, 1);
        Set(handle, Attribute.WindowCornerPreference, (int)CornerPreference.Round);

        // 0x00BBGGRR — matches Surface.Border (#262A33).
        Set(handle, Attribute.BorderColor, 0x00332A26);

        var wanted = requested;
        if (wanted != BackdropKind.None && !TransparencyEffectsEnabled())
        {
            wanted = BackdropKind.None;
        }

        if (wanted == BackdropKind.None)
        {
            Set(handle, Attribute.SystemBackdropType, 1);   // DWMSBT_NONE
            ClearFrameExtension(handle);
            SetCompositionTransparent(window, false);
            return false;
        }

        // The backdrop is only visible through pixels WPF leaves unpainted, so the
        // composition target itself has to be transparent. Without this the window
        // renders opaque and the DWM attribute silently appears to do nothing.
        if (!SetCompositionTransparent(window, true))
        {
            return false;
        }

        // Extend the frame over the title bar and status bar only.
        //
        // Using -1 ("whole client area is glass") is the usual recipe, but it composites
        // the region occupied by the terminal's child HWND too. That produces a visible
        // flash on every tab open or switch, because any frame where the child hasn't
        // painted yet shows straight through to the desktop.
        var dpi = VisualTreeHelper.GetDpi(window);
        var margins = new Margins
        {
            Left = 0,
            Right = 0,
            Top = (int)Math.Ceiling(topBandDip * dpi.DpiScaleY),
            Bottom = (int)Math.Ceiling(bottomBandDip * dpi.DpiScaleY),
        };

        if (DwmExtendFrameIntoClientArea(handle, ref margins) != 0)
        {
            SetCompositionTransparent(window, false);
            return false;
        }

        if (!Set(handle, Attribute.SystemBackdropType, (int)wanted))
        {
            ClearFrameExtension(handle);
            SetCompositionTransparent(window, false);
            return false;
        }

        return true;
    }

    private static void ClearFrameExtension(IntPtr handle)
    {
        var none = default(Margins);
        DwmExtendFrameIntoClientArea(handle, ref none);
    }

    private static bool SetCompositionTransparent(Window window, bool transparent)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source ||
            source.CompositionTarget is null)
        {
            return false;
        }

        source.CompositionTarget.BackgroundColor = transparent ? Colors.Transparent : Colors.Black;
        return true;
    }

    private static bool Set(IntPtr handle, Attribute attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, (int)attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;   // pre-Win10 or a stripped SKU: the window just looks plainer
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
