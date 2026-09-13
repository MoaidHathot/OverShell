using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OverShell.App.Chrome;

/// <summary>
/// A second top-level window holding one live tab (DESIGN.md §12.12). The tab's surface
/// is moved here from the main window and back — same HWND, same session, verified by
/// spike 2 (§12.7) — so nothing about the terminal restarts. The tab stays in the main
/// window's collection: detection, the sidebar, the dashboard and notifications carry
/// on. Closing this window re-attaches; it never ends the session.
/// </summary>
public sealed class TearOffWindow : Window
{
    private readonly Grid _host;
    private bool _reattachOnClose = true;

    public TearOffWindow(Window owner, TerminalTab tab)
    {
        Tab = tab;
        Title = $"{tab.Label} — OverShell";
        Width = Math.Max(600, owner.ActualWidth * 0.7);
        Height = Math.Max(400, owner.ActualHeight * 0.7);
        Left = owner.Left + 60;
        Top = owner.Top + 60;
        Background = tab.Background;
        ShowInTaskbar = true;
        MinWidth = 400;
        MinHeight = 240;

        // Standard chrome on purpose: the main window's custom caption exists for its tab
        // strip; a tear-off has one tab and the title bar names it.
        WindowStyle = WindowStyle.SingleBorderWindow;

        _host = new Grid { Background = tab.Background };
        Content = _host;

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TerminalTab.Label) or nameof(TerminalTab.Title))
            {
                Title = $"{tab.Label} — OverShell";
            }
        };

        Activated += (_, _) => Tab.Surface.Focus();
    }

    public TerminalTab Tab { get; }

    /// <summary>Raised when the window closes with the surface still inside it; the main window takes it back.</summary>
    public event Action<TerminalTab>? ReattachRequested;

    /// <summary>Puts the tab's view into this window. The main window has already removed it from its host.</summary>
    public void Host()
    {
        _host.Children.Add(Tab.View);
        Tab.Detached = true;
    }

    /// <summary>Takes the view out again for the main window; closing afterwards does not re-attach twice.</summary>
    public FrameworkElement Release()
    {
        _reattachOnClose = false;
        _host.Children.Remove(Tab.View);
        Tab.Detached = false;
        return Tab.View;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_reattachOnClose && !e.Cancel)
        {
            ReattachRequested?.Invoke(Tab);
        }
    }

    /// <summary>The terminal's own HWND, for the router and diagnostics.</summary>
    public nint TerminalHwnd => MainWindow.FindTerminalHwnd(Tab.View);

    /// <summary>The host's colour follows the tab's scheme so a resize never flashes another colour.</summary>
    public Brush HostBackground => _host.Background;
}
