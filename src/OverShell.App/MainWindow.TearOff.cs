using System.Windows;
using System.Windows.Interop;
using OverShell.App.Chrome;

namespace OverShell.App;

/// <summary>
/// Tear-off windows (DESIGN.md §12.12): a tab's live surface moved into a window of its
/// own and back. The tab stays in <see cref="MainWindow.Tabs"/> throughout, so everything
/// that watches tabs keeps working; only where the pixels land changes.
/// </summary>
public partial class MainWindow
{
    private readonly List<TearOffWindow> _tearOffs = [];
    private TerminalTab? _commandTarget;

    internal IReadOnlyList<TearOffWindow> TearOffs => _tearOffs;

    /// <summary>
    /// The tab a command acts on: the tear-off's tab while a chord arrives from a tear-off
    /// window, else the main window's active tab.
    /// </summary>
    internal TerminalTab? TargetTab => _commandTarget ?? ActiveTab;

    private void InitializeTearOff()
    {
        _commands.Register("tab.detach", "Detach tab into its own window", "Tabs", () => { if (TargetTab is { } t) Detach(t); }, () => TargetTab is { Detached: false } && Tabs.Count > 1, "The live terminal moves; the session is untouched");
        _commands.Register("tab.attach", "Attach tab back to the main window", "Tabs", () => { if (TargetTab is { } t) Attach(t); }, () => TargetTab is { Detached: true });
    }

    /// <summary>Moves a tab's surface into a new window. The main window shows its neighbour meanwhile.</summary>
    internal TearOffWindow? Detach(TerminalTab tab)
    {
        if (tab.Detached || Tabs.Count < 2)
        {
            return null;
        }

        // Pick what the main window shows next before the view leaves.
        if (ReferenceEquals(tab, ActiveTab))
        {
            var index = Tabs.IndexOf(tab);
            var next = Tabs.Where((t, i) => i != index && !t.Detached).OrderBy(t => Math.Abs(Tabs.IndexOf(t) - index)).FirstOrDefault();
            if (next is null)
            {
                return null;
            }

            ActiveTab = next;
        }

        TerminalHost.Children.Remove(tab.View);
        tab.View.Visibility = Visibility.Visible;

        var window = new TearOffWindow(this, tab);
        window.Host();
        window.ReattachRequested += OnTearOffClosing;
        window.Activated += (_, _) => UpdateViewed();
        window.Deactivated += (_, _) => UpdateViewed();
        _tearOffs.Add(window);
        _shortcuts.AddWindow(window);
        window.Show();

        _trace.Write($"[{tab.Id}] detached into window 0x{new WindowInteropHelper(window).Handle:X} (terminal 0x{window.TerminalHwnd:X})");
        UpdateViewed();
        return window;
    }

    /// <summary>Brings a tab's surface back into the main host and makes it the active tab.</summary>
    internal void Attach(TerminalTab tab)
    {
        var window = _tearOffs.FirstOrDefault(w => ReferenceEquals(w.Tab, tab));
        if (window is null)
        {
            return;
        }

        var view = window.Release();
        _tearOffs.Remove(window);
        window.ReattachRequested -= OnTearOffClosing;
        window.Close();

        TerminalHost.Children.Add(view);
        ActiveTab = tab;
        _trace.Write($"[{tab.Id}] attached back (terminal 0x{FindTerminalHwnd(tab.View):X})");
        UpdateViewed();
    }

    /// <summary>The user closed a tear-off with the X: the tab comes home rather than dying.</summary>
    private void OnTearOffClosing(TerminalTab tab)
    {
        var window = _tearOffs.FirstOrDefault(w => ReferenceEquals(w.Tab, tab));
        if (window is null)
        {
            return;
        }

        var view = window.Release();
        _tearOffs.Remove(window);
        TerminalHost.Children.Add(view);
        if (!tab.IsRunning && tab.HasStarted)
        {
            // A dead shell coming back is not worth showing over the live one.
            return;
        }

        ActiveTab = tab;
        _trace.Write($"[{tab.Id}] tear-off closed; attached back");
    }

    /// <summary>The tear-off, if any, whose root HWND is <paramref name="root"/>.</summary>
    private TearOffWindow? TearOffForRoot(nint root) =>
        root == 0 ? null : _tearOffs.FirstOrDefault(w => new WindowInteropHelper(w).Handle == root);

    /// <summary>The tab whose terminal HWND is <paramref name="hwnd"/>, detached or not.</summary>
    private TerminalTab? TabForTerminalHwnd(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        foreach (var tab in Tabs)
        {
            if (tab.TerminalHwnd == hwnd)
            {
                return tab;
            }
        }

        return null;
    }

    /// <summary>A tear-off window is where its tab is looked at.</summary>
    private bool IsViewedInTearOff(TerminalTab tab) =>
        tab.Detached && _tearOffs.Any(w => ReferenceEquals(w.Tab, tab) && w.IsActive);

    private void CloseTearOffs()
    {
        foreach (var window in _tearOffs.ToArray())
        {
            window.ReattachRequested -= OnTearOffClosing;
            window.Release();
            window.Close();
        }

        _tearOffs.Clear();
    }
}
