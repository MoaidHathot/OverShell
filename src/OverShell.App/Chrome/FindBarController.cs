using System.Windows;
using System.Windows.Threading;
using OverShell.App.Terminal.Search;

namespace OverShell.App.Chrome;

/// <summary>
/// Binds a <see cref="FindBar"/> to the tab it searches (DESIGN.md §12.14). One per window
/// that hosts tabs - the main window and each tear-off - so a chord pressed in a tear-off
/// searches the tab in front of the user. The session is per tab and is replaced when the
/// window's active tab changes while the bar is open; closing the bar ends it and gives
/// the keyboard back to the terminal.
/// </summary>
internal sealed class FindBarController
{
    private readonly FindBar _bar;
    private readonly Window _owner;
    private readonly TerminalSearch _search;
    private FindSession? _session;

    public FindBarController(FindBar bar, Window owner, TerminalSearch search)
    {
        _bar = bar;
        _owner = owner;
        _search = search;

        bar.QueryChanged += (needle, matchCase) => _session?.SetQuery(needle, matchCase);
        bar.StepRequested += direction => _session?.Step(direction);
        bar.CloseRequested += Close;

        // Highlights are positioned in screen pixels: stale the moment the window moves, and
        // in the way when another window covers the terminal.
        owner.LocationChanged += (_, _) => _session?.Refresh();
        owner.SizeChanged += (_, _) => _session?.Refresh();
        owner.Activated += (_, _) => _session?.Resume();
        owner.Deactivated += (_, _) => _session?.Suspend();
    }

    public bool IsOpen => _bar.Visibility == Visibility.Visible;

    /// <summary>The live session, for diagnostics and the self-test.</summary>
    internal FindSession? Session => _session;

    /// <summary>Shows the bar over <paramref name="tab"/>, searching what the box already holds.</summary>
    public void Open(TerminalTab tab)
    {
        Bind(tab);
        _bar.Visibility = Visibility.Visible;
        _owner.Dispatcher.BeginInvoke(_bar.FocusInput, DispatcherPriority.Input);
    }

    public void Close()
    {
        var tab = _session?.Tab;
        _bar.Visibility = Visibility.Collapsed;
        _session?.Dispose();
        _session = null;
        tab?.Surface.Focus();
    }

    /// <summary>The window now shows another tab; the open bar follows it.</summary>
    public void Retarget(TerminalTab? tab)
    {
        if (!IsOpen)
        {
            return;
        }

        if (tab is null)
        {
            Close();
            return;
        }

        Bind(tab);
    }

    /// <summary>A tab that is going away takes its session with it; the bar stays for the next active tab.</summary>
    public void Forget(TerminalTab tab)
    {
        if (_session?.Tab != tab)
        {
            return;
        }

        _session.Dispose();
        _session = null;
        _bar.StatusText = string.Empty;
        _bar.SetCanStep(false);
    }

    private void Bind(TerminalTab tab)
    {
        if (ReferenceEquals(_session?.Tab, tab))
        {
            return;
        }

        _session?.Dispose();
        _session = new FindSession(tab, _owner, _search);
        _session.Changed += OnChanged;
        _session.SetQuery(_bar.Text, _bar.IsMatchCase);
        OnChanged(_session);
    }

    private void OnChanged(FindSession session)
    {
        if (!ReferenceEquals(session, _session))
        {
            return;
        }

        _bar.StatusText = session.Status;
        _bar.SetCanStep(session.Count > 0);
    }
}
