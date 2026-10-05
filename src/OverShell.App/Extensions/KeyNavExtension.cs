using OverShell.Core.Extensibility;
using OverShell.Core.Input;

namespace OverShell.App.Extensions;

/// <summary>
/// Keyboard navigation of the sidebar and the dashboard (DESIGN.md §12.16): a cursor
/// separate from the active tab. <c>sidebar.focus</c> / <c>dashboard.focus</c> (herd mode
/// <c>s</c> / <c>c</c>, or the view's own chord pressed again) put the cursor on the active
/// tab; ↑↓ / j k move it, Home/End jump, Enter activates (the dashboard then opens the tab
/// in the terminal view), Space activates and keeps the cursor, Esc returns to the
/// terminal. While the cursor is up the extension owns those keys; everything else goes
/// where it always went.
/// </summary>
public sealed class KeyNavExtension : IExtension
{
    private IShell _shell = null!;
    private Pane _pane = Pane.None;

    public string Id => "keynav";

    internal enum Pane
    {
        None,
        Sidebar,
        Dashboard,
    }

    internal Pane Active => _pane;

    public void Initialize(IShell shell)
    {
        _shell = shell;
        shell.Commands.Register("sidebar.focus", "Sidebar: move the cursor into it", "View", () => Enter(Pane.Sidebar), () => shell.SidebarVisible,
            "Arrows or j/k move, Enter switches to the tab, Esc returns to the terminal");
        shell.Commands.Register("dashboard.focus", "Dashboard: move the cursor into it", "View", () => Enter(Pane.Dashboard), () => shell.DashboardVisible,
            "Arrows or j/k move, Enter opens the tab, Esc returns to the terminal");
        shell.Commands.Register("nav.leave", "Leave the sidebar / dashboard cursor", "View", Leave, () => _pane != Pane.None);

        shell.Keys.AddDefaults(
        [
            new Keybinding($"{HerdModeExtension.Leader} s", "sidebar.focus"),
            new Keybinding($"{HerdModeExtension.Leader} c", "dashboard.focus"),
        ]);

        shell.Keys.Intercept(() => _pane != Pane.None, OnChord);
        shell.TabClosed += tab => { if (ReferenceEquals(shell.Cursor, tab)) Move(0); };
        shell.SettingsChanged += () => { if (_pane != Pane.None && !PaneVisible(_pane)) Leave(); };
    }

    private bool PaneVisible(Pane pane) => pane switch
    {
        Pane.Sidebar => _shell.SidebarVisible,
        Pane.Dashboard => _shell.DashboardVisible,
        _ => false,
    };

    private IReadOnlyList<ITab> Order() => _pane == Pane.Sidebar ? _shell.SidebarOrder : _shell.Tabs;

    internal void Enter(Pane pane)
    {
        if (!PaneVisible(pane))
        {
            _shell.Ui.Status(pane == Pane.Sidebar ? "The sidebar is not showing in this view" : "The dashboard is not showing in this view");
            return;
        }

        _pane = pane;
        var order = Order();
        var start = _shell.ActiveTab is { } active && order.Contains(active) ? active : order.FirstOrDefault();
        _shell.SetCursor(start);
        _shell.Trace($"keynav: cursor into the {pane.ToString().ToLowerInvariant()} on {start?.Label ?? "nothing"}");
    }

    internal void Leave()
    {
        if (_pane == Pane.None)
        {
            return;
        }

        _pane = Pane.None;
        _shell.SetCursor(null);
        if (_shell.ActiveTab is { } active)
        {
            _shell.Activate(active); // refocuses the terminal
        }

        _shell.Trace("keynav: cursor left");
    }

    private void Move(int delta)
    {
        var order = Order();
        if (order.Count == 0)
        {
            _shell.SetCursor(null);
            return;
        }

        var index = _shell.Cursor is { } cursor ? order.ToList().IndexOf(cursor) : -1;
        var next = index < 0 ? 0 : Math.Clamp(index + delta, 0, order.Count - 1);
        _shell.SetCursor(order[next]);
    }

    private void Jump(bool end)
    {
        var order = Order();
        if (order.Count > 0)
        {
            _shell.SetCursor(end ? order[^1] : order[0]);
        }
    }

    private bool OnChord(KeyChord chord)
    {
        if (chord.Modifiers is not (ChordModifiers.None or ChordModifiers.Shift))
        {
            // A real chord (Ctrl+..., the leader) is not ours: leave the cursor so the chord lands cleanly.
            Leave();
            return false;
        }

        switch (chord.Key)
        {
            case "Down" or "J":
                Move(1);
                return true;
            case "Up" or "K":
                Move(-1);
                return true;
            case "Home":
                Jump(end: false);
                return true;
            case "End":
                Jump(end: true);
                return true;
            case "Return":
                if (_shell.Cursor is { } chosen)
                {
                    var pane = _pane;
                    Leave();
                    _shell.Activate(chosen);
                    if (pane == Pane.Dashboard)
                    {
                        _shell.ShowView("terminal");
                    }
                }

                return true;
            case "Space":
                if (_shell.Cursor is { } peek)
                {
                    _shell.Activate(peek);
                    _shell.SetCursor(peek);
                }

                return true;
            case "Escape":
                Leave();
                return true;
            default:
                // Any other key: back to the terminal, and the key goes with it.
                Leave();
                return false;
        }
    }

    public void Dispose() => Leave();
}
