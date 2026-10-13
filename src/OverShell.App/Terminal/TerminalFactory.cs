using OverShell.App.Terminal.ConPty;
using OverShell.App.Terminal.WindowsTerminal;

namespace OverShell.App.Terminal;

/// <summary>
/// How the terminal body is drawn (DESIGN.md §12.20): through the control's child HWND as always
/// (<see cref="Enabled"/> false), or through a composition visual that keeps its alpha, so the
/// window's backdrop shows through the default background at <see cref="Opacity"/>. The mode is
/// chosen once, at start, because a terminal's HWND is created once; the opacity follows the
/// settings live.
/// </summary>
internal sealed record TerminalComposition(bool Enabled, double Opacity)
{
    public static readonly TerminalComposition Opaque = new(false, 1.0);

    /// <summary>What <c>window.terminalOpacity</c> asks for: composed when below 1 and a backdrop is in play.</summary>
    public static TerminalComposition From(double terminalOpacity, bool backdropAvailable)
    {
        var opacity = double.IsFinite(terminalOpacity) ? Math.Clamp(terminalOpacity, 0.0, 1.0) : 1.0;
        return opacity < 1.0 && backdropAvailable ? new TerminalComposition(true, opacity) : Opaque;
    }
}

/// <summary>
/// The one place that decides which session and surface implementations a tab gets.
/// Today there is a single pair; a profile-level switch goes here when a second surface
/// exists (DESIGN.md §11.4).
/// </summary>
internal static class TerminalFactory
{
    /// <summary>Set by the main window from the settings before the first tab; every surface created afterwards follows it.</summary>
    public static TerminalComposition Composition { get; set; } = TerminalComposition.Opaque;

    /// <summary>
    /// The body's opacity while its window is not the active one (<c>window.terminalOpacityUnfocused</c>,
    /// §12.24). Windows paints acrylic for the active window only and a flat grey behind the others,
    /// so the default, 1, makes an inactive body the theme's own background - opaque, what Windows
    /// Terminal's acrylic falls back to - instead of that grey seen through the translucency.
    /// </summary>
    public static double UnfocusedOpacity { get; set; } = 1.0;

    /// <summary>What a surface in a window should be at: the configured opacity when the window is active, the unfocused one otherwise; 1 when not composed.</summary>
    public static double BodyOpacityFor(bool windowActive) =>
        !Composition.Enabled ? 1.0 : windowActive ? Composition.Opacity : Math.Clamp(UnfocusedOpacity, 0.0, 1.0);

    public static (ITerminalSession Session, ITerminalSurface Surface) Create(SessionDescriptor descriptor) =>
        (new ConPtySession(descriptor), new WindowsTerminalSurface(Composition));
}