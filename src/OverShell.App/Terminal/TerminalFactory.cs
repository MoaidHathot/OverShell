using OverShell.App.Terminal.ConPty;
using OverShell.App.Terminal.WindowsTerminal;

namespace OverShell.App.Terminal;

/// <summary>
/// The one place that decides which session and surface implementations a tab gets.
/// Today there is a single pair; a profile-level switch goes here when a second surface
/// exists (DESIGN.md §11.4).
/// </summary>
internal static class TerminalFactory
{
    public static (ITerminalSession Session, ITerminalSurface Surface) Create(SessionDescriptor descriptor) =>
        (new ConPtySession(descriptor), new WindowsTerminalSurface());
}
