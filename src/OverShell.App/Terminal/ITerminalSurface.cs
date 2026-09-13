using System.Windows;
using OverShell.Config;

namespace OverShell.App.Terminal;

/// <summary>What a surface can do, so the chrome offers only what will work.</summary>
[Flags]
public enum SurfaceCapabilities
{
    None = 0,

    /// <summary>
    /// The surface owns a native child HWND. WPF cannot draw over it (airspace), keyboard
    /// messages bypass WPF's input stack, and Tab/arrows must be forwarded by hand.
    /// </summary>
    NativeHwnd = 1 << 0,

    /// <summary>A live session can be detached and re-attached (viewport only, see DESIGN.md §7.1).</summary>
    LiveReattach = 1 << 1,

    /// <summary>The terminal body can be translucent; per-profile opacity/acrylic may be honoured.</summary>
    Transparency = 1 << 2,

    /// <summary>Ordinary WPF content can be composed over the terminal body.</summary>
    Overlays = 1 << 3,

    /// <summary>The surface has its own find-in-buffer UI.</summary>
    Search = 1 << 4,

    /// <summary>The surface detects and opens hyperlinks itself.</summary>
    Hyperlinks = 1 << 5,
}

/// <summary>Pointer shapes the chrome may ask a surface to show over the terminal body.</summary>
public enum TerminalPointer
{
    /// <summary>Whatever the surface shows on its own — an I-beam for the native control.</summary>
    Default,

    /// <summary>Something under the pointer can be clicked.</summary>
    Hand,
}

/// <summary>
/// Where a surface's renderer draws an underline within a cell: distance from the cell's
/// top edge to the line's top, and the line's thickness, both in physical pixels.
/// </summary>
public readonly record struct TerminalUnderline(int Top, int Thickness);

/// <summary>
/// Turns a session into pixels and input into bytes. Engine and renderer are deliberately
/// fused here: the default implementation is Microsoft's terminal control, which cannot
/// be split any finer than this (DESIGN.md §11.3).
/// </summary>
public interface ITerminalSurface : IDisposable
{
    /// <summary>The element the chrome places in the layout. Its Margin and Visibility belong to the host.</summary>
    FrameworkElement View { get; }

    SurfaceCapabilities Capabilities { get; }

    (int Columns, int Rows) Grid { get; }

    /// <summary>The pointer to show over the terminal body. Set by the chrome, e.g. while hovering a link.</summary>
    TerminalPointer Pointer { get; set; }

    /// <summary>
    /// Where this surface's renderer would draw an underline in a cell of the given
    /// physical size. Null when the surface cannot say for these dimensions — the caller
    /// then falls back to a generic bar near the cell's bottom.
    /// </summary>
    /// <param name="cellSize">Cell size actually observed on screen, physical pixels.</param>
    /// <param name="renderedFontFamily">The family the renderer reports using, when known.</param>
    TerminalUnderline? GetUnderline(Size cellSize, string? renderedFontFamily);

    /// <summary>
    /// Raised on the UI thread once the surface has something to draw. The reveal gate
    /// from DESIGN.md §7.5 waits for this.
    /// </summary>
    event EventHandler? Ready;

    /// <summary>
    /// Binds a session to this surface. A session that has not started yet is started as
    /// soon as the surface knows its grid size.
    /// </summary>
    void Attach(ITerminalSession session);

    void Detach();

    void ApplyTheme(ColorScheme scheme, TerminalProfile profile);

    void Focus();

    /// <summary>Selected text, or empty. Clears the selection, matching the native control.</summary>
    string GetSelectedText();
}
