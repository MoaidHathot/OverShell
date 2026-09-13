using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Terminal.Wpf;
using OverShell.Config;

namespace OverShell.App.Terminal.WindowsTerminal;

/// <summary>
/// The default surface: Microsoft's <see cref="TerminalControl"/> — the genuine Windows
/// Terminal parser, buffer and AtlasEngine renderer inside a native child HWND — hosted
/// directly, with an <see cref="ITerminalSession"/> plugged in through
/// <see cref="SessionConnection"/>.
/// <para>
/// Sequence on load: connect the control first (so nothing the child prints is lost),
/// then start the session with the grid the control has measured, then — once the
/// child exists — request win32-input-mode on the terminal side and push the exact grid
/// size to the pseudoconsole, which is what fixes the partially-off first layout.
/// </para>
/// </summary>
internal sealed class WindowsTerminalSurface : ITerminalSurface
{
    private const string DefaultFontFace = "Cascadia Mono, Consolas";
    private const double DefaultFontSize = 12;

    private const int WmSetCursor = 0x0020;
    private const int WmMouseMove = 0x0200;
    private const int HtClient = 1;
    private const int IdcHand = 32649;

    private readonly TerminalControl _control = new() { AutoResize = true };
    private readonly SessionConnection _connection = new();

    private ITerminalSession? _session;
    private HwndHost? _container;
    private (ColorScheme Scheme, TerminalProfile Profile)? _theme;
    private TerminalPointer _pointer;
    private bool _loaded;
    private bool _connected;
    private bool _readyRaised;

    public WindowsTerminalSurface()
    {
        // Same navigation modes the previous wrapper used. They do not stop WPF from
        // claiming Tab and the arrows — ShortcutRouter handles that — but they keep focus
        // from wandering to a sibling when it does.
        KeyboardNavigation.SetTabNavigation(_control, KeyboardNavigationMode.Contained);
        KeyboardNavigation.SetDirectionalNavigation(_control, KeyboardNavigationMode.Contained);

        _control.Loaded += OnLoaded;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    public FrameworkElement View => _control;

    public SurfaceCapabilities Capabilities =>
        SurfaceCapabilities.NativeHwnd | SurfaceCapabilities.LiveReattach;

    public (int Columns, int Rows) Grid => (_control.Columns, _control.Rows);

    /// <summary>
    /// The native window sets its class cursor on every <c>WM_SETCURSOR</c>, so an override
    /// has to be applied from inside that message. <see cref="HwndHost.MessageHook"/> runs
    /// before the original window procedure, which is exactly the hook needed.
    /// </summary>
    public TerminalPointer Pointer
    {
        get => _pointer;
        set
        {
            if (_pointer == value)
            {
                return;
            }

            _pointer = value;

            // Re-run cursor selection now rather than at the next mouse move.
            if (_container is { Handle: var hwnd } && hwnd != IntPtr.Zero)
            {
                SendMessageW(hwnd, WmSetCursor, hwnd, (IntPtr)(HtClient | (WmMouseMove << 16)));
            }
        }
    }

    public event EventHandler? Ready;

    /// <summary>
    /// The renderer's own underline for this font, provided the font it resolved is the one
    /// we think it is — which the observed cell size settles (see <see cref="AtlasFontMetrics"/>).
    /// </summary>
    public TerminalUnderline? GetUnderline(Size cellSize, string? renderedFontFamily)
    {
        if (_theme is not var (_, profile))
        {
            return null;
        }

        var dpi = (int)VisualTreeHelper.GetDpi(_control).PixelsPerInchX;
        var metrics = AtlasFontMetrics.Resolve(
            profile.FontFace ?? DefaultFontFace,
            renderedFontFamily,
            FontSizeFor(profile),
            dpi);

        if (metrics is null ||
            metrics.CellWidth != (int)Math.Round(cellSize.Width) ||
            metrics.CellHeight != (int)Math.Round(cellSize.Height))
        {
            return null;
        }

        return new TerminalUnderline(metrics.UnderlineTop, metrics.UnderlineThickness);
    }

    /// <summary>Diagnostics: the metrics as resolved, before the cell-size check.</summary>
    internal AtlasFontMetrics.Result? DescribeFontMetrics(string? renderedFontFamily) =>
        _theme is var (_, profile)
            ? AtlasFontMetrics.Resolve(profile.FontFace ?? DefaultFontFace, renderedFontFamily, FontSizeFor(profile), (int)VisualTreeHelper.GetDpi(_control).PixelsPerInchX)
            : null;

    /// <summary>The control takes a short; this is the value it was given.</summary>
    private static short FontSizeFor(TerminalProfile profile) => (short)Math.Round(profile.FontSize ?? DefaultFontSize);

    public void Attach(ITerminalSession session)
    {
        Detach();

        _session = session;
        _connection.Bind(session);
        session.Started += OnSessionStarted;

        if (_loaded)
        {
            Connect();
        }
    }

    public void Detach()
    {
        if (_session is { } session)
        {
            session.Started -= OnSessionStarted;
        }

        _connection.Unbind();
        _session = null;

        if (_connected)
        {
            // Hides the cursor and stops the control writing into a session it no longer owns.
            _control.Connection = null!;
            _connected = false;
        }
    }

    public void ApplyTheme(ColorScheme scheme, TerminalProfile profile)
    {
        _theme = (scheme, profile);

        if (_loaded)
        {
            ApplyThemeCore();
        }
    }

    public void Focus() => _control.Focus();

    public string GetSelectedText() => _control.GetSelectedText();

    public void Dispose()
    {
        _control.Loaded -= OnLoaded;

        if (_container is { } container)
        {
            container.MessageHook -= OnContainerMessage;
            _container = null;
        }

        Detach();
    }

    // -------------------------------------------------------------- internals

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;

        if (_container is null && FindDescendant<HwndHost>(_control) is { } container)
        {
            _container = container;
            container.MessageHook += OnContainerMessage;
        }

        // SetTheme is a no-op until the control has a PresentationSource, hence here.
        ApplyThemeCore();
        Connect();
    }

    private IntPtr OnContainerMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSetCursor && _pointer == TerminalPointer.Hand && ((long)lParam & 0xFFFF) == HtClient)
        {
            SetCursor(LoadCursorW(IntPtr.Zero, IdcHand));
            handled = true;
            return 1;
        }

        return IntPtr.Zero;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void Connect()
    {
        if (_session is not { } session || _connected)
        {
            return;
        }

        // The control resets the screen and shows the cursor when a connection is
        // assigned; from this point every TerminalOutput reaches the native buffer.
        _control.Connection = _connection;
        _connected = true;

        if (!session.HasStarted)
        {
            var (columns, rows) = Grid;
            session.Start(Math.Max(columns, 2), Math.Max(rows, 2));
        }
        else
        {
            // Re-attaching a live session: same post-start handshake as a fresh one.
            OnSessionStarted(session, EventArgs.Empty);
        }
    }

    private void ApplyThemeCore()
    {
        if (_theme is not var (scheme, profile))
        {
            return;
        }

        _control.SetTheme(
            scheme.ToTerminalTheme(profile.CursorShape),
            profile.FontFace ?? DefaultFontFace,
            FontSizeFor(profile));
    }

    /// <summary>Runs on the session's I/O thread.</summary>
    private void OnSessionStarted(object? sender, EventArgs e)
    {
        // DECSET 9001: ask the terminal to encode keys as win32-input-mode records, which
        // carry everything an INPUT_RECORD does. ConPTY understands them unconditionally;
        // the previous wrapper did exactly this, so behaviour is unchanged.
        _connection.Inject("\x1b[?9001h");

        _control.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _session))
            {
                return;
            }

            var (columns, rows) = Grid;
            if (columns > 0 && rows > 0)
            {
                _session?.Resize(columns, rows);
            }

            if (!_readyRaised)
            {
                _readyRaised = true;
                Ready?.Invoke(this, EventArgs.Empty);
            }
        });
    }
}
