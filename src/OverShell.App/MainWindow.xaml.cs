using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OverShell.App.Overlay;
using OverShell.App.Terminal;
using OverShell.App.Terminal.Hyperlinks;
using OverShell.App.Terminal.WindowsTerminal;
using OverShell.Config;
using OverShell.Core.Settings;

namespace OverShell.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ProfileCatalog _catalog;
    private readonly DispatcherTimer _statusTimer;
    private readonly ShortcutRouter _shortcuts;
    private readonly TerminalTextProbe _textProbe = new();
    private TerminalTab? _activeTab;
    private OverlayHost? _overlay;

    // Link hover: any pointer movement over the terminal looks up the text underneath —
    // the same as Windows Terminal, which underlines on plain hover and needs Ctrl only to
    // follow. Probes are serialised and coalesced, and skipped while the pointer stays in
    // the cell (or on the link) it last asked about and nothing has been printed since, so
    // a resting or slowly moving mouse costs nothing. The last *resolved* result also lets
    // a click be decided without another probe.
    private TerminalMouseEvent? _hoverPending;
    private TerminalMouseEvent? _lastPointer;
    private (Point Point, LinkHit? Link, long Timestamp, long OutputVersion)? _lastResolved;
    private (Point Origin, Size Cell)? _cellGrid;
    private bool _hoverBusy;
    private bool _controlDown;
    private TerminalTab? _hoverTab;
    private string? _hoverLink;
    private IReadOnlyList<Rect> _hoverRects = [];

    // Ctrl+click whose target is not yet known: the button messages are held back while
    // the text under the pointer is read, then either dropped (a link opened) or handed to
    // the terminal after all, in order (a selection starts a few milliseconds late).
    private readonly List<TerminalMouseEvent> _heldMouse = [];
    private DispatcherTimer? _heldMouseTimeout;
    private bool _clickPending;
    private bool _swallowNextLeftUp;

    // The native window has no CS_DBLCLKS: the second press of a double-click arrives as an
    // ordinary button-down, so repeats of the same link within the double-click interval
    // are one gesture.
    private (string Url, long Timestamp)? _lastOpened;

    /// <summary>A hover result older than this is re-checked at click time rather than trusted.</summary>
    private static readonly TimeSpan HoverFreshness = TimeSpan.FromMilliseconds(300);

    /// <summary>A pointer that stays put is looked at again no more often than this.</summary>
    private static readonly TimeSpan HoverRefresh = TimeSpan.FromMilliseconds(150);

    public MainWindow()
    {
        _catalog = ProfileCatalog.Load();

        // Settings, rules, persisted labels and the integration endpoint come first: the
        // first tab's child process needs the endpoint's variables in its environment.
        InitializeHerd();

        InitializeComponent();
        DataContext = this;

        _shortcuts = new ShortcutRouter(this);
        RegisterCommands();
        LoadKeybindings();
        WireRouter();
        InitializeViews();

        // Single terminal on launch — panes and extra tabs are opt-in.
        if (_catalog.DefaultProfile is { } profile)
        {
            AddTab(profile, activate: true);
        }

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        StateChanged += (_, _) => SyncMaximizeState();
        SourceInitialized += (_, _) =>
        {
            ApplyBackdrop();
            HookSettingChange();
        };

        // The frame-extension margins are physical pixels, so they have to be
        // recomputed whenever the window lands on a monitor with a different scale.
        DpiChanged += (_, _) => ApplyBackdrop();

        // Anything drawn over the terminal is positioned in screen pixels and goes stale
        // the moment the window moves; the hover it belongs to is cheap to re-establish.
        LocationChanged += (_, _) => ClearLinkHover();
        SizeChanged += (_, _) => ClearLinkHover();
        Deactivated += (_, _) => ClearLinkHover();

        // "Viewed" means the active tab in a foreground window; Done clears only then.
        Activated += (_, _) => UpdateViewed();
        Deactivated += (_, _) => UpdateViewed();

        Loaded += (_, _) =>
        {
            SyncMaximizeState();
            InitializeNotifications();
            UpdateViewed();
            ActiveTab?.Surface.Focus();
        };
    }

    public ObservableCollection<TerminalTab> Tabs { get; } = [];

    public ProfileCatalog Catalog => _catalog;

    public TerminalTab? ActiveTab
    {
        get => _activeTab;
        internal set
        {
            if (ReferenceEquals(_activeTab, value))
            {
                return;
            }

            ClearLinkHover();

            if (_activeTab is not null)
            {
                _activeTab.IsActive = false;
            }

            _activeTab = value;

            if (_activeTab is not null)
            {
                _activeTab.IsActive = true;
                Title = $"{_activeTab.Title} — OverShell";
            }

            Raise();
            UpdateViewed();
            UpdateStatus();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ------------------------------------------------------------ tab model

    internal TerminalTab AddTab(TerminalProfile profile, bool activate)
    {
        var tab = new TerminalTab(profile, _catalog.SchemeFor(profile), Dispatcher, _agents);
        tab.UserLabel = _state.LabelFor(profile.Id, tab.WorkingDirectory);

        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalTab.Title) && ReferenceEquals(tab, ActiveTab))
            {
                Title = $"{tab.Title} — OverShell";
            }
        };
        tab.AttentionRequested += OnAttention;
        tab.StateChanged += (_, _) => RefreshAttention();

        // A dashboard or an open switcher wants this tab's screen from the start.
        tab.ScreenWatched = _settings.ViewFor(_viewId).Content == ViewContent.Dashboard || _palette is not null;

        Tabs.Add(tab);
        _tabsById[tab.Id] = tab;
        TerminalHost.Children.Add(tab.View);

        if (activate)
        {
            ActiveTab = tab;
        }

        if (Tabs.Count == 1)
        {
            ScheduleLinkSelfProbe(tab);
            Diagnostics.Spikes.Schedule(this, tab);
            Diagnostics.HerdSelfTest.Schedule(this, tab);
        }

        return tab;
    }

    internal void CloseTab(TerminalTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        // Dispose first: it suppresses further input, so the focus and key messages
        // generated while the HwndHost is unloaded can't reach a closed pseudoconsole.
        tab.Dispose();

        Tabs.RemoveAt(index);
        _tabsById.Remove(tab.Id);
        _notifications?.Viewed(tab.Id);
        TerminalHost.Children.Remove(tab.View);

        if (Tabs.Count == 0)
        {
            Close();
            return;
        }

        if (ReferenceEquals(tab, ActiveTab))
        {
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        RefreshAttention();
    }

    private void ActivateRelative(int delta)
    {
        if (ActiveTab is null || Tabs.Count < 2)
        {
            return;
        }

        var index = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[index];
    }

    // ------------------------------------------------------------- commands

    private void WireRouter()
    {
        var router = _shortcuts;

        // Hand-delivering Tab and the arrow keys is only necessary — and only correct —
        // when the focused element is a native child HWND (DESIGN.md §7.2c).
        router.ForwardNavigationKeys = () =>
            ActiveTab?.Surface.Capabilities.HasFlag(SurfaceCapabilities.NativeHwnd) == true;

        // Hovering previews the link under the pointer; Ctrl turns the pointer into a hand
        // and Ctrl+click opens it.
        router.TerminalMouse = OnTerminalMouse;
        router.PointerLeftTerminal = OnPointerLeftTerminal;
        router.ControlPressed = OnControlPressed;
        router.ControlReleased = OnControlReleased;

        // Every chord goes through keybindings.jsonc → command; nothing is hard-coded here.
        router.Chord = OnChord;

        // Classic console behaviour: right-click copies a selection, else pastes.
        router.RightClick = screenPoint =>
        {
            if (ActiveTab is null || !IsPointOverTerminal(screenPoint))
            {
                return false;
            }

            if (!Copy())
            {
                Paste();
            }

            return true;
        };
    }

    /// <summary>
    /// Hit-tests a screen point against the terminal area. Uses screen coordinates from
    /// the message itself rather than <see cref="Mouse.GetPosition"/>, whose state is
    /// stale while the native terminal owns the pointer.
    /// </summary>
    private bool IsPointOverTerminal(Point screenPoint)
    {
        if (!IsLoaded || TerminalHost.ActualWidth <= 0)
        {
            return false;
        }

        var topLeft = TerminalHost.PointToScreen(new Point(0, 0));
        var bottomRight = TerminalHost.PointToScreen(
            new Point(TerminalHost.ActualWidth, TerminalHost.ActualHeight));

        return screenPoint.X >= topLeft.X && screenPoint.X <= bottomRight.X &&
               screenPoint.Y >= topLeft.Y && screenPoint.Y <= bottomRight.Y;
    }

    /// <returns>True when a selection existed and was copied.</returns>
    private bool Copy()
    {
        var selection = ActiveTab?.SelectedText();
        if (string.IsNullOrEmpty(selection))
        {
            return false;
        }

        TrySetClipboard(selection);
        return true;
    }

    private void Paste()
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsText())
            {
                tab.Paste(Clipboard.GetText());
            }
        }
        catch (ExternalException)
        {
            // Another process had the clipboard locked; nothing useful to do.
        }
    }

    private static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Clipboard contention — swallow rather than crash the shell.
        }
    }

    // ------------------------------------------------------------ hyperlinks

    /// <summary>
    /// Hovering over the terminal underlines the link under the pointer and shows its
    /// target in the status bar; holding Ctrl turns the pointer into a hand; Ctrl+click
    /// opens it instead of starting a selection — unless the application has claimed the
    /// mouse (vim, tmux…), in which case every click is its business and no hints are
    /// shown. A plain click on a link starts a selection, as it always has.
    /// <para>
    /// The text is read back through UI Automation on a worker thread, so a click cannot
    /// be decided synchronously unless a fresh hover probe already answered for this
    /// pointer — the common case, since the pointer got there by moving. Otherwise the
    /// button messages are held back while a probe runs, and either dropped (link opened)
    /// or redelivered to the terminal in order (selection starts a few milliseconds late).
    /// Nothing is ever guessed.
    /// </para>
    /// </summary>
    private bool OnTerminalMouse(TerminalMouseEvent e)
    {
        // MK_CONTROL on the message is the truth for that instant — including a Ctrl that
        // went down while the pointer was over some other window, which no key message told
        // us about.
        _controlDown = e.Control;

        if (_clickPending)
        {
            // Keep the sequence intact until the decision is made.
            _heldMouse.Add(e);
            return true;
        }

        switch (e.Kind)
        {
            case TerminalMouseKind.LeftDown or TerminalMouseKind.LeftDoubleClick:
                if (!e.Control || e.Shift || ActiveTab is not { MouseTracking: false } tab)
                {
                    return false;
                }

                if (_lastResolved is { } resolved && Stopwatch.GetElapsedTime(resolved.Timestamp) < HoverFreshness)
                {
                    // The terminal's own rectangles for the link hover found: a press inside
                    // them is a press on that link, wherever the pointer drifted since.
                    if (resolved.Link is { } known && known.Rects.Any(r => r.Contains(e.ScreenPoint)))
                    {
                        _swallowNextLeftUp = true;
                        LinkTrace($"click {e.ScreenPoint} -> on hovered link {known.Match.Url}");
                        OpenLinkOnce(known.Match.Url);
                        return true;
                    }

                    // Hover looked at exactly this point and found nothing: an ordinary click.
                    if (resolved.Link is null && resolved.Point == e.ScreenPoint)
                    {
                        LinkTrace($"click {e.ScreenPoint} -> pass-through (hover found no link here)");
                        return false;
                    }
                }

                BeginHeldClick(tab, e);
                return true;

            case TerminalMouseKind.LeftUp:
                if (!_swallowNextLeftUp)
                {
                    return false;
                }

                _swallowNextLeftUp = false;
                return true;

            default:
                // A drag that began with a swallowed press stays swallowed: the terminal never
                // learned of the press, so it must not see the drag either.
                if (_swallowNextLeftUp && e.LeftButtonDown)
                {
                    return true;
                }

                _lastPointer = e;

                if (e.LeftButtonDown || ActiveTab is not { MouseTracking: false })
                {
                    // A selection is being dragged, or the application owns the mouse.
                    ClearLinkHover();
                }
                else
                {
                    QueueHoverProbe(e);
                }

                if (_hoverTab is { } hovered)
                {
                    UpdatePointer(hovered);
                }

                return false;
        }
    }

    /// <summary>
    /// Ctrl went down: the pointer becomes a hand if it is on a link, and a pointer that has
    /// been resting on the terminal is looked at again in case output moved underneath it.
    /// </summary>
    private void OnControlPressed()
    {
        _controlDown = true;

        if (_hoverTab is { } hovered)
        {
            UpdatePointer(hovered);
        }

        if (_shortcuts.PointerOverTerminal && _lastPointer is { LeftButtonDown: false } pointer && ActiveTab is { MouseTracking: false })
        {
            QueueHoverProbe(pointer with { Control = true });
        }
    }

    /// <summary>Ctrl went up: the hand goes; the underline stays for as long as the pointer does.</summary>
    private void OnControlReleased()
    {
        _controlDown = false;

        if (_hoverTab is { } hovered)
        {
            UpdatePointer(hovered);
        }
    }

    private void OnPointerLeftTerminal()
    {
        _lastPointer = null;
        ClearLinkHover();
    }

    private void QueueHoverProbe(TerminalMouseEvent e)
    {
        _hoverPending = e;
        if (!_hoverBusy)
        {
            _ = RunHoverProbeAsync();
        }
    }

    private async Task RunHoverProbeAsync()
    {
        _hoverBusy = true;
        try
        {
            while (_hoverPending is { } e && ActiveTab is { } tab)
            {
                _hoverPending = null;

                if (!ShouldProbe(tab, e.ScreenPoint))
                {
                    continue;
                }

                var version = tab.OutputVersion;
                var started = Stopwatch.GetTimestamp();
                var result = await _textProbe.ProbeAsync(e.Hwnd, e.ScreenPoint, tab.Grid.Columns, tab.ResolveLink);
                LinkTrace($"hover {e.ScreenPoint} -> {Describe(result)} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");

                // The tab may have changed under us while the probe was running.
                if (!ReferenceEquals(tab, ActiveTab))
                {
                    break;
                }

                if (result?.CellGrid is { } grid)
                {
                    _cellGrid = grid;
                }

                _lastResolved = (e.ScreenPoint, result?.Link, Stopwatch.GetTimestamp(), version);
                SetLinkHover(tab, result?.Link);
            }
        }
        finally
        {
            _hoverBusy = false;
        }
    }

    /// <summary>
    /// Whether the text under <paramref name="point"/> could differ from the last answer:
    /// something was printed since, the answer is old, or the pointer crossed into another
    /// cell that is not part of the link it was on.
    /// </summary>
    private bool ShouldProbe(TerminalTab tab, Point point)
    {
        if (_lastResolved is not { } last)
        {
            return true;
        }

        if (last.OutputVersion != tab.OutputVersion || Stopwatch.GetElapsedTime(last.Timestamp) > HoverRefresh)
        {
            return true;
        }

        if (last.Link is { } link && link.Rects.Any(r => r.Contains(point)))
        {
            return false;
        }

        return _cellGrid is not { } grid || CellAt(grid, point) != CellAt(grid, last.Point);
    }

    private static (int Column, int Row) CellAt((Point Origin, Size Cell) grid, Point point) =>
        ((int)Math.Floor((point.X - grid.Origin.X) / grid.Cell.Width),
         (int)Math.Floor((point.Y - grid.Origin.Y) / grid.Cell.Height));

    /// <summary>
    /// Output arrived while the pointer rests on the terminal: what is underneath it may have
    /// scrolled. Called from the status timer, so at most twice a second, and only while the
    /// pointer really is still where we last saw it — a pointer that left the window without
    /// telling us (it happens) must not grow a phantom underline.
    /// </summary>
    private void RefreshRestingHover(TerminalTab tab)
    {
        if (!IsActive || _hoverBusy || tab.MouseTracking ||
            _lastPointer is not { LeftButtonDown: false } pointer ||
            _lastResolved is not { } last || last.OutputVersion == tab.OutputVersion ||
            ShortcutRouter.CursorPosition() != pointer.ScreenPoint)
        {
            return;
        }

        QueueHoverProbe(pointer);
    }

    // ------------------------------------------------------ held Ctrl+click

    private void BeginHeldClick(TerminalTab tab, TerminalMouseEvent down)
    {
        _clickPending = true;
        _heldMouse.Clear();
        _heldMouse.Add(down);

        // Fail open: if the probe never answers, the terminal gets its click after all.
        _heldMouseTimeout ??= new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(300) };
        _heldMouseTimeout.Tick -= OnHeldClickTimeout;
        _heldMouseTimeout.Tick += OnHeldClickTimeout;
        _heldMouseTimeout.Stop();
        _heldMouseTimeout.Start();

        LinkTrace($"click {down.ScreenPoint} -> no fresh hover answer here; holding");
        _ = ResolveHeldClickAsync(tab, down);
    }

    private async Task ResolveHeldClickAsync(TerminalTab tab, TerminalMouseEvent down)
    {
        ProbeResult? result = null;
        var started = Stopwatch.GetTimestamp();
        try
        {
            result = await _textProbe.ProbeAsync(down.Hwnd, down.ScreenPoint, tab.Grid.Columns, tab.ResolveLink);
        }
        finally
        {
            LinkTrace($"click {down.ScreenPoint} -> {Describe(result)} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
            FinishHeldClick(tab, result?.Link);
        }
    }

    private void OnHeldClickTimeout(object? sender, EventArgs e)
    {
        LinkTrace("click -> probe timed out");
        FinishHeldClick(null, null);
    }

    private void FinishHeldClick(TerminalTab? tab, LinkHit? link)
    {
        if (!_clickPending)
        {
            return;
        }

        _clickPending = false;
        _heldMouseTimeout?.Stop();

        var held = _heldMouse.ToArray();
        _heldMouse.Clear();

        if (link is not null && tab is not null)
        {
            // Dropped on purpose: the terminal never learns a click happened. If the
            // release has not arrived yet it must be swallowed when it does.
            _swallowNextLeftUp = !held.Any(m => m.Kind == TerminalMouseKind.LeftUp);
            _lastResolved = (held[0].ScreenPoint, link, Stopwatch.GetTimestamp(), tab.OutputVersion);
            SetLinkHover(tab, link);
            LinkTrace($"click -> opened {link.Match.Url}; {held.Length} held message(s) dropped");
            OpenLinkOnce(link.Match.Url);
            return;
        }

        LinkTrace($"click -> no link; {held.Length} held message(s) redelivered to the terminal");
        foreach (var message in held)
        {
            ShortcutRouter.Redeliver(message);
        }
    }

    /// <summary>Opens a link unless the same one was opened within the double-click interval.</summary>
    private void OpenLinkOnce(string url)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastOpened is { } last &&
            string.Equals(last.Url, url, StringComparison.Ordinal) &&
            Stopwatch.GetElapsedTime(last.Timestamp, now) < ShortcutRouter.DoubleClickTime)
        {
            LinkTrace("click -> repeat within double-click time; not opened again");
            return;
        }

        _lastOpened = (url, now);
        OpenLink(url);
    }

    // -------------------------------------------------------------- hover UI

    private void SetLinkHover(TerminalTab tab, LinkHit? link)
    {
        var url = link?.Match.Url;
        var rects = link?.Rects ?? [];

        if (_hoverTab is not null && !ReferenceEquals(_hoverTab, tab))
        {
            _hoverTab.Surface.Pointer = TerminalPointer.Default;
        }

        _hoverTab = tab;

        if (!string.Equals(_hoverLink, url, StringComparison.Ordinal))
        {
            _hoverLink = url;
            TxtLinkHint.Text = url ?? string.Empty;
            UpdateDetailSlot();
        }

        UpdatePointer(tab);

        if (_hoverRects.SequenceEqual(rects))
        {
            return;
        }

        _hoverRects = rects;

        if (rects.Count == 0 || link is null)
        {
            _overlay?.HideOverlay();
            return;
        }

        // The terminal's own colour for that text when it is uniform, else the scheme's.
        var brush = link.Foreground is { } color ? new SolidColorBrush(color) : tab.Foreground;
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        // Every rect is one row of cells, so its height is the cell height; the row's width
        // over the grid's column count is the cell width (never the text length — a row
        // with wide glyphs has fewer characters than cells).
        var cell = CellSize(tab, link);
        var underline = tab.Surface.GetUnderline(cell, link.FontFamily);

        (_overlay ??= new OverlayHost(this)).ShowUnderline(rects, brush, underline);
    }

    private static Size CellSize(TerminalTab tab, LinkHit link)
    {
        var columns = tab.Grid.Columns > 0 ? tab.Grid.Columns : link.Text.Rows[link.Text.HitRow].Length;
        return new Size(
            columns > 0 ? link.Text.HitRowBounds.Width / columns : 0,
            link.Rects.Count > 0 ? link.Rects[0].Height : link.Text.HitRowBounds.Height);
    }

    /// <summary>A hand only when there is a link under the pointer *and* Ctrl would follow it.</summary>
    private void UpdatePointer(TerminalTab tab) =>
        tab.Surface.Pointer = _hoverLink is not null && _controlDown ? TerminalPointer.Hand : TerminalPointer.Default;

    private void ClearLinkHover()
    {
        _hoverPending = null;
        _lastResolved = null;
        _cellGrid = null;

        if (_hoverTab is { } tab)
        {
            SetLinkHover(tab, null);
            _hoverTab = null;
        }
    }

    /// <summary>Hands a URL to the shell's default handler. Only schemes that name something openable.</summary>
    private static void OpenLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (uri.Scheme is not ("http" or "https" or "ftp" or "file" or "mailto"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            // No handler registered, or the target is gone. Not worth a dialog.
        }
    }

    private static string Describe(ProbeResult? result)
    {
        if (result?.Text is not { } t)
        {
            return result is null ? "hit=none" : "hit=none (below last row)";
        }

        var text = $"hit=row {t.HitRow}/{t.Rows.Count} offset {t.Offset} row-len {t.Rows[t.HitRow].Length} " +
                   $"row-rect=[{t.HitRowBounds.X},{t.HitRowBounds.Y} {t.HitRowBounds.Width}x{t.HitRowBounds.Height}]" +
                   (t.TruncatedAbove || t.TruncatedBelow ? " truncated" : string.Empty);

        if (result.Link is not { } link)
        {
            return text + " link=none";
        }

        var rects = string.Join(" ", link.Rects.Select(r => $"[{r.X},{r.Y} {r.Width}x{r.Height}]"));
        var color = link.Foreground is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "mixed";
        return $"{text} link={link.Match.Url} span={link.Match.Start}+{link.Match.Length} rects={rects} fg={color} font='{link.FontFamily}'";
    }

    /// <summary>
    /// Set <c>OVERSHELL_TRACE_LINKS=1</c> to log every hover and click resolution to
    /// <c>%TEMP%\overshell-links.log</c>, and to run a self-test against the first tab a
    /// moment after it is ready: a full probe of the banner row as if its first word were
    /// a link, which exercises the text walk, the terminal-side search and geometry, the
    /// colour and font attributes, the renderer-metric reconstruction, and the overlay —
    /// all without anyone touching the mouse.
    /// </summary>
    private static readonly bool LinkTraceEnabled =
        Environment.GetEnvironmentVariable("OVERSHELL_TRACE_LINKS") == "1";

    private static void LinkTrace(string message)
    {
        if (!LinkTraceEnabled)
        {
            return;
        }

        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overshell-links.log"),
                $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Tracing must never break input handling.
        }
    }

    private void ScheduleLinkSelfProbe(TerminalTab tab)
    {
        if (!LinkTraceEnabled)
        {
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();

            if (!ReferenceEquals(tab, ActiveTab) || !tab.View.IsVisible)
            {
                LinkTrace("self-probe skipped: tab not visible");
                return;
            }

            // The first text row, a few cells in — where the shell's banner sits.
            var origin = tab.View.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(tab.View);
            var point = new Point(origin.X + (60 * dpi.DpiScaleX), origin.Y + (10 * dpi.DpiScaleY));
            var hwnd = FindTerminalHwnd(tab.View);

            // Treat the first word of the line as a link, so every downstream step runs.
            static LinkMatch? FirstWordAsLink(string line, int offset)
            {
                var length = line.TrimEnd().IndexOf(' ');
                length = length < 0 ? line.TrimEnd().Length : length;
                return length > 0 && offset < length ? new LinkMatch("https://overshell.invalid/self-test", 0, length) : null;
            }

            var started = Stopwatch.GetTimestamp();
            var result = hwnd == 0 ? null : await _textProbe.ProbeAsync(hwnd, point, tab.Grid.Columns, FirstWordAsLink);
            var elapsed = Stopwatch.GetElapsedTime(started);

            LinkTrace($"self-probe: hwnd=0x{hwnd:X} {Describe(result)} grid={tab.Grid.Columns}x{tab.Grid.Rows} in {elapsed.TotalMilliseconds:F1} ms" +
                      (result?.Text is null ? string.Empty : $"; line='{result.Text.Line.TrimEnd()}'") +
                      (result?.CellGrid is { } g ? $"; cell-grid origin=({g.Origin.X},{g.Origin.Y}) cell={g.Cell.Width:F2}x{g.Cell.Height:F2}" : string.Empty));

            // The empty area below the last row: no text, but the lattice must still come back,
            // or the hover throttle has nothing to work with on a fresh shell.
            var below = hwnd == 0 ? null : await _textProbe.ProbeAsync(hwnd, new Point(point.X, origin.Y + (400 * dpi.DpiScaleY)), tab.Grid.Columns, FirstWordAsLink);
            LinkTrace($"self-probe below last row: {Describe(below)}" +
                      (below?.CellGrid is { } bg ? $"; cell-grid cell={bg.Cell.Width:F2}x{bg.Cell.Height:F2}" : "; NO cell grid"));

            if (result?.Link is not { } link)
            {
                return;
            }

            if (tab.Surface is WindowsTerminalSurface wt)
            {
                var metrics = wt.DescribeFontMetrics(link.FontFamily);
                var cell = CellSize(tab, link);
                var underline = tab.Surface.GetUnderline(cell, link.FontFamily);
                LinkTrace(metrics is null
                    ? "self-test metrics: font not resolved"
                    : $"self-test metrics: family='{metrics.Family}' predicted cell {metrics.CellWidth}x{metrics.CellHeight} observed {cell.Width:F2}x{cell.Height:F2} " +
                      $"baseline {metrics.Baseline} underline top {metrics.UnderlineTop} thickness {metrics.UnderlineThickness} -> {(underline is null ? "REJECTED (fallback bar)" : "accepted")}");
            }

            var focusBefore = ShortcutRouter.FocusedWindow();
            SetLinkHover(tab, link);
            var focusAfter = ShortcutRouter.FocusedWindow();

            LinkTrace($"self-test overlay: {_overlay?.Describe() ?? "no overlay"}; " +
                      $"focus before=0x{focusBefore:X} after=0x{focusAfter:X} terminal=0x{hwnd:X} " +
                      $"{(focusBefore == focusAfter ? "unchanged" : "CHANGED")}");

            await Task.Delay(1500);
            ClearLinkHover();
            LinkTrace($"self-test overlay hidden: {_overlay?.Describe() ?? "no overlay"}");
        };
        timer.Start();
    }

    internal static nint FindTerminalHwnd(DependencyObject root)
    {
        if (root is HwndHost host)
        {
            return host.Handle;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindTerminalHwnd(VisualTreeHelper.GetChild(root, i));
            if (found != 0)
            {
                return found;
            }
        }

        return 0;
    }

    private void NewTab(TerminalProfile? profile)
    {
        if (profile is not null)
        {
            AddTab(profile, activate: true);
        }
    }

    // -------------------------------------------------------- event handlers

    private void ShowProfilesMenu(Button button)
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("ShellContextMenu"),
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        var itemStyle = (Style)FindResource("ShellMenuItem");

        foreach (var profile in _catalog.Profiles)
        {
            var item = new MenuItem
            {
                Header = profile.Name,
                Style = itemStyle,
                DataContext = new MenuAccent(TabAccent.For(profile.Id)),
                InputGestureText = profile.Id == _catalog.DefaultProfile?.Id ? "default" : string.Empty,
            };

            var captured = profile;
            item.Click += (_, _) => NewTab(captured);
            menu.Items.Add(item);
        }

        if (_catalog.Diagnostics.Count > 0)
        {
            menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
            menu.Items.Add(new MenuItem
            {
                Header = $"{_catalog.Diagnostics.Count} profile(s) unavailable",
                Style = itemStyle,
                IsEnabled = false,
            });
        }

        menu.IsOpen = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // Restore under the cursor, the way every native title bar behaves.
            var cursor = e.GetPosition(this);
            var ratio = ActualWidth > 0 ? cursor.X / ActualWidth : 0.5;

            WindowState = WindowState.Normal;

            var screen = PointToScreen(cursor);
            Left = screen.X - (RestoreBounds.Width * ratio);
            Top = screen.Y - (e.GetPosition(this).Y / 2);
        }

        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        _reloadTimer?.Stop();
        _configWatcher?.Dispose();
        _shortcuts.Dispose();
        _palette?.Close();
        _notifications?.Dispose();
        _endpoint?.Dispose();

        // Two passes on purpose: WPF unloads every HwndHost during shutdown and each one
        // generates focus traffic, so all tabs must stop accepting input before any of
        // them starts closing pipes.
        foreach (var tab in Tabs)
        {
            tab.SuppressInput();
        }

        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }

        base.OnClosed(e);
    }

    // --------------------------------------------------------------- chrome

    /// <summary>
    /// Applies the system backdrop and swaps the chrome brushes to match. If the
    /// backdrop is unavailable — old build, transparency effects switched off, DWM
    /// refusing — we fall back to the fully opaque chrome rather than rendering a
    /// washed-out translucent tint over nothing.
    /// </summary>
    private void ApplyBackdrop()
    {
        // The bands follow the layout: a slim caption without tabs, no status band in Zen.
        var top = CaptionHeight;
        var bottom = _statusVisible ? (double)FindResource("Metrics.StatusBarHeight") : 0;

        var active = WindowChromeInterop.Apply(this, WindowChromeInterop.Resolve(), top, bottom);

        // With no backdrop the composition target is opaque, so the root has to paint
        // the base surface itself — otherwise the window renders flat black.
        WindowRoot.Background = active ? Brushes.Transparent : (Brush)FindResource("Surface.Base");

        TitleBarSurface.Background = (Brush)FindResource(
            active ? "Surface.ChromeTranslucent" : "Surface.Chrome");
        StatusBarSurface.Background = (Brush)FindResource(
            active ? "Surface.StatusTranslucent" : "Surface.Chrome");

        // ClearType cannot be applied over a transparent surface; asking for it anyway
        // produces colour fringing. Be explicit rather than relying on WPF's fallback.
        TextOptions.SetTextRenderingMode(
            this,
            active ? TextRenderingMode.Grayscale : TextRenderingMode.ClearType);
    }

    /// <summary>
    /// Toggling "Transparency effects" in Settings broadcasts WM_SETTINGCHANGE.
    /// Re-applying keeps the chrome correct without needing a restart.
    /// </summary>
    private void HookSettingChange()
    {
        const int WmSettingChange = 0x001A;
        const int WmDwmCompositionChanged = 0x031E;

        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg is WmSettingChange or WmDwmCompositionChanged)
            {
                Dispatcher.BeginInvoke(ApplyBackdrop, DispatcherPriority.Background);
            }

            return IntPtr.Zero;
        });
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void SyncMaximizeState()
    {
        var maximized = WindowState == WindowState.Maximized;

        BtnMaximize.Content = maximized ? "\uE923" : "\uE922";
        BtnMaximize.ToolTip = maximized ? "Restore" : "Maximize";

        // A maximized WindowChrome window overhangs the work area by its resize border.
        // Derive the compensation from the system metric rather than hard-coding it, so
        // it stays correct across DPI and theme changes.
        var resize = SystemParameters.WindowResizeBorderThickness;
        WindowRoot.Margin = maximized
            ? new Thickness(resize.Left, resize.Top, resize.Right, resize.Bottom)
            : default;

        WindowRoot.BorderThickness = maximized ? default : new Thickness(1);
    }

    /// <summary>The window's one heartbeat, twice a second: status text, exit detection, agent timers, hover refresh.</summary>
    private void UpdateStatus()
    {
        var now = DateTimeOffset.Now;

        // Only a tab that actually started can have died. The PTY starts on a background
        // thread, so a plain !IsRunning check would fire on every healthy new tab.
        foreach (var tab in Tabs)
        {
            if (tab.HasStarted && !tab.IsRunning)
            {
                tab.NotifyExited();
            }

            tab.Heartbeat(now);
        }

        RefreshAttention();
        RefreshViews(now);

        if (ActiveTab is not { } active)
        {
            TxtGrid.Text = string.Empty;
            return;
        }

        var (columns, rows) = active.Grid;
        var tabCount = Tabs.Count == 1 ? string.Empty : $"   ·   {Tabs.Count} tabs";
        TxtGrid.Text = $"{columns}\u00d7{rows}{tabCount}";

        RefreshRestingHover(active);
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property ?? string.Empty));

    /// <summary>Binding shim so the menu item template can show a profile's accent dot.</summary>
    private sealed record MenuAccent(System.Windows.Media.Brush Accent);
}
