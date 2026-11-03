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
internal sealed class FindBarController : IDisposable
{
    private readonly FindBar _bar;
    private readonly Window _owner;
    private readonly TerminalSearch _search;
    private FindSession? _session;
    private bool _paused;

    public FindBarController(FindBar bar, Window owner, TerminalSearch search)
    {
        _bar = bar;
        _owner = owner;
        _search = search;

        bar.QueryChanged += OnQueryChanged;
        bar.StepRequested += OnStepRequested;
        bar.CloseRequested += Close;

        // Highlights are positioned in screen pixels: stale the moment the window moves, and
        // in the way when another window covers the terminal.
        owner.LocationChanged += OnLocationChanged;
        owner.SizeChanged += OnSizeChanged;
        owner.Activated += OnActivated;
        owner.Deactivated += OnDeactivated;
    }

    public bool IsOpen => _bar.Visibility == Visibility.Visible;
    internal bool InputHasFocus => _bar.IsKeyboardFocusWithin;
    internal void FocusInput() => _bar.FocusInput();
    internal bool Paused => _paused;
    internal void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused) _session?.Suspend(); else if (_owner.IsActive) _session?.Resume();
    }

    private void OnQueryChanged(string needle, bool matchCase) => _session?.SetQuery(needle, matchCase);
    private void OnStepRequested(int direction) => _session?.Step(direction);
    private void OnLocationChanged(object? sender, EventArgs e) { if (!_paused) _session?.Refresh(); }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) { if (!_paused) _session?.Refresh(); }
    private void OnActivated(object? sender, EventArgs e) { if (!_paused) _session?.Resume(); }
    private void OnDeactivated(object? sender, EventArgs e) => _session?.Suspend();

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _bar.QueryChanged -= OnQueryChanged;
        _bar.StepRequested -= OnStepRequested;
        _bar.CloseRequested -= Close;
        _owner.LocationChanged -= OnLocationChanged;
        _owner.SizeChanged -= OnSizeChanged;
        _owner.Activated -= OnActivated;
        _owner.Deactivated -= OnDeactivated;
    }

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
