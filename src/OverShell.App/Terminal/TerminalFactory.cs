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
    /// §12.24, §12.25), or null for the same as the active one - the default: an inactive window
    /// looks exactly like an active one, as Windows Terminal's does (the window keeps its frame
    /// active for DWM, so the acrylic stays - WindowChromeInterop.HookNcActivate); 1 makes the
    /// inactive body the theme's own background, opaque, instead.
    /// </summary>
    public static double? UnfocusedOpacity { get; set; }

    /// <summary>What a surface in a window should be at: the configured opacity when the window is active, the unfocused one (when set) otherwise; 1 when not composed.</summary>
    public static double BodyOpacityFor(bool windowActive) =>
        !Composition.Enabled ? 1.0 : windowActive || UnfocusedOpacity is null ? Composition.Opacity : Math.Clamp(UnfocusedOpacity.Value, 0.0, 1.0);

    /// <summary>The unfocused value a settings object asks for: a finite number clamped to 0..1, or null for "same as the body".</summary>
    public static double? UnfocusedFrom(double? setting) => setting is { } value && double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : null;

    public static (ITerminalSession Session, ITerminalSurface Surface) Create(SessionDescriptor descriptor) =>
        (new ConPtySession(descriptor), new WindowsTerminalSurface(Composition));
}