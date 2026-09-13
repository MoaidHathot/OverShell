using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace OverShell.App;

internal enum TerminalMouseKind
{
    Move,
    LeftDown,
    LeftDoubleClick,
    LeftUp,
}

/// <summary>
/// A mouse message addressed to a native terminal window, in screen pixels, together with
/// the raw message so it can be redelivered unchanged (<see cref="ShortcutRouter.Redeliver"/>).
/// </summary>
internal readonly record struct TerminalMouseEvent(
    nint Hwnd,
    TerminalMouseKind Kind,
    Point ScreenPoint,
    bool Control,
    bool Shift,
    bool LeftButtonDown,
    int Message,
    nint WParam,
    nint LParam);

/// <summary>
/// Application input routing that works over a hosted native terminal.
/// <para>
/// The Windows Terminal control owns its own child HWND and forwards every
/// <c>WM_KEYDOWN</c> straight into the terminal, so WPF <see cref="InputBinding"/>s on
/// the window never fire — the shell swallows Ctrl+T before WPF sees it. Hooking
/// <see cref="ComponentDispatcher.ThreadPreprocessMessage"/> lets us claim a small set
/// of reserved chords before <c>DispatchMessage</c> hands them to the terminal. The same
/// hook sees the mouse messages posted to that HWND, which is how Ctrl+click on a link
/// is intercepted before the control turns it into a selection.
/// </para>
/// </summary>
internal sealed class ShortcutRouter : IDisposable
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int MkLButton = 0x0001;
    private const int MkShift = 0x0004;
    private const int MkControl = 0x0008;
    private const uint GaRoot = 2;

    // Bit 30 of a WM_KEYDOWN lParam: the key was already down (auto-repeat).
    private const long KeyWasDownFlag = 0x40000000;

    /// <summary>Window class HwndTerminal.cpp registers for the native terminal.</summary>
    private const string TerminalWindowClass = "HwndTerminalClass";

    // Virtual keys that never form a chord on their own.
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkProcessKey = 0xE5;
    private const int VkPacket = 0xE7;

    private readonly List<Binding> _bindings = [];

    private IntPtr _windowHandle;
    private bool _pointerOverTerminal;
    private bool _disposed;

    public ShortcutRouter(Window window)
    {
        _windowHandle = new WindowInteropHelper(window).Handle;

        if (_windowHandle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) =>
                _windowHandle = new WindowInteropHelper(window).Handle;
        }

        ComponentDispatcher.ThreadPreprocessMessage += OnPreprocessMessage;
    }

    /// <summary>
    /// Invoked on right-click, with the click point in screen coordinates.
    /// Return true to swallow it. Terminals traditionally map this to copy-or-paste.
    /// </summary>
    public Func<Point, bool>? RightClick { get; set; }

    /// <summary>
    /// Mouse traffic addressed to a native terminal window. Return true to swallow the
    /// message so the control never sees it (no selection starts, for instance).
    /// </summary>
    public Func<TerminalMouseEvent, bool>? TerminalMouse { get; set; }

    /// <summary>The pointer moved from a terminal window onto the chrome.</summary>
    public Action? PointerLeftTerminal { get; set; }

    /// <summary>True while the last mouse message seen was addressed to a terminal window.</summary>
    public bool PointerOverTerminal => _pointerOverTerminal;

    /// <summary>Ctrl went down (not auto-repeat) — a hover hint that depends on it may start.</summary>
    public Action? ControlPressed { get; set; }

    /// <summary>Ctrl was released — a hover hint that depended on it should go.</summary>
    public Action? ControlReleased { get; set; }

    /// <summary>
    /// Whether Tab and the arrow keys should be hand-delivered to the focused child HWND.
    /// Only true when the active surface is a native window; a managed surface gets them
    /// through WPF's ordinary input stack. Defaults to forwarding.
    /// </summary>
    public Func<bool>? ForwardNavigationKeys { get; set; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Win32Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    /// <summary>The window with keyboard focus on this thread. Diagnostics only.</summary>
    internal static IntPtr FocusedWindow() => GetFocus();

    /// <summary>The system's double-click interval: two presses closer than this are one gesture.</summary>
    internal static TimeSpan DoubleClickTime => TimeSpan.FromMilliseconds(GetDoubleClickTime());

    /// <summary>Where the pointer is right now, in physical screen pixels — the same space as <see cref="MSG.pt_x"/>.</summary>
    internal static Point CursorPosition() =>
        GetCursorPos(out var p) ? new Point(p.X, p.Y) : new Point(double.NaN, double.NaN);

    /// <summary>
    /// Keys WPF treats as focus navigation but a terminal must receive verbatim.
    /// </summary>
    private static readonly Key[] NavigationKeys =
    [
        Key.Tab, Key.Left, Key.Right, Key.Up, Key.Down,
    ];

    /// <param name="handler">Return true to swallow the key, false to let it reach the shell.</param>
    public void Add(Key key, ModifierKeys modifiers, Func<bool> handler) =>
        _bindings.Add(new Binding(key, modifiers, handler));

    public void Add(Key key, ModifierKeys modifiers, Action action) =>
        Add(key, modifiers, () => { action(); return true; });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ComponentDispatcher.ThreadPreprocessMessage -= OnPreprocessMessage;
    }

    private void OnPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (handled)
        {
            return;
        }

        switch (msg.message)
        {
            case WmKeyDown or WmSysKeyDown:
                if (!IsOurs(msg.hwnd))
                {
                    break;
                }

                if (((long)msg.wParam & 0xFF) == VkControl)
                {
                    if (((long)msg.lParam & KeyWasDownFlag) == 0)
                    {
                        ControlPressed?.Invoke();
                    }
                }
                else
                {
                    OnKeyDown(ref msg, ref handled);
                }

                break;

            case WmKeyUp:
                if (((long)msg.wParam & 0xFF) == VkControl && IsOurs(msg.hwnd))
                {
                    ControlReleased?.Invoke();
                }

                break;

            case WmRButtonUp:
                if (IsOurs(msg.hwnd) && RightClick?.Invoke(new Point(msg.pt_x, msg.pt_y)) == true)
                {
                    handled = true;
                }

                break;

            case WmMouseMove or WmLButtonDown or WmLButtonDblClk or WmLButtonUp:
                if (IsOurs(msg.hwnd))
                {
                    OnMouse(ref msg, ref handled);
                }

                break;
        }
    }

    /// <summary>Only ever act on input genuinely destined for this window.</summary>
    private bool IsOurs(IntPtr hwnd) =>
        _windowHandle != IntPtr.Zero && GetAncestor(hwnd, GaRoot) == _windowHandle;

    private void OnKeyDown(ref MSG msg, ref bool handled)
    {
        var virtualKey = (int)((long)msg.wParam & 0xFF);

        // VK_PACKET / VK_PROCESSKEY come from injected-Unicode or IME paths and carry no
        // usable key identity. Bare modifiers can't form a chord either.
        if (virtualKey is VkPacket or VkProcessKey or VkShift or VkControl or VkMenu or VkLWin or VkRWin)
        {
            return;
        }

        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        if (key == Key.None)
        {
            return;
        }

        var modifiers = CurrentModifiers();
        Trace(virtualKey, key, modifiers);

        foreach (var binding in _bindings)
        {
            if (binding.Key != key || binding.Modifiers != modifiers)
            {
                continue;
            }

            if (binding.Handler())
            {
                handled = true;
            }

            return;
        }

        ForwardNavigationKey(ref msg, ref handled, key, modifiers);
    }

    private void OnMouse(ref MSG msg, ref bool handled)
    {
        if (!IsTerminalWindow(msg.hwnd))
        {
            if (_pointerOverTerminal && msg.message == WmMouseMove)
            {
                _pointerOverTerminal = false;
                PointerLeftTerminal?.Invoke();
            }

            return;
        }

        _pointerOverTerminal = true;

        var kind = msg.message switch
        {
            WmLButtonDown => TerminalMouseKind.LeftDown,
            WmLButtonDblClk => TerminalMouseKind.LeftDoubleClick,
            WmLButtonUp => TerminalMouseKind.LeftUp,
            _ => TerminalMouseKind.Move,
        };

        var flags = (long)msg.wParam;
        var e = new TerminalMouseEvent(
            msg.hwnd,
            kind,
            new Point(msg.pt_x, msg.pt_y),
            Control: (flags & MkControl) != 0,
            Shift: (flags & MkShift) != 0,
            LeftButtonDown: (flags & MkLButton) != 0,
            msg.message,
            msg.wParam,
            msg.lParam);

        if (TerminalMouse?.Invoke(e) == true)
        {
            handled = true;
        }
    }

    /// <summary>
    /// Hands a previously swallowed mouse message to its window after all, unchanged.
    /// <para>
    /// This is the user's own message going to the window it was addressed to, a little
    /// late — the same mechanism as Tab forwarding, not synthetic input (DESIGN.md §7.8).
    /// <c>SendMessageW</c> calls the window procedure directly, so the pre-dispatch hook
    /// does not see it a second time.
    /// </para>
    /// </summary>
    public static void Redeliver(TerminalMouseEvent e) =>
        SendMessageW(e.Hwnd, e.Message, e.WParam, e.LParam);

    private static bool IsTerminalWindow(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        var length = GetClassNameW(hwnd, name, name.Capacity);
        return length == TerminalWindowClass.Length &&
               string.Equals(name.ToString(), TerminalWindowClass, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hands Tab and the arrow keys straight to the terminal instead of letting WPF
    /// turn them into focus navigation.
    /// <para>
    /// Setting <c>KeyboardNavigation.TabNavigation</c> does not help: every mode either
    /// cycles focus among the control's children or skips past the container to the next
    /// focusable element, and in both cases WPF marks the key handled — so the message is
    /// never dispatched and the shell never sees Tab.
    /// </para>
    /// <para>
    /// <c>SendMessageW</c> invokes the child's window procedure directly rather than
    /// posting, so the message bypasses the queue and this hook does not observe it a
    /// second time. Claiming it afterwards stops WPF from moving focus.
    /// </para>
    /// </summary>
    private void ForwardNavigationKey(ref MSG msg, ref bool handled, Key key, ModifierKeys modifiers)
    {
        // Only bare or Shift-modified presses; Ctrl/Alt combinations are ours or the app's.
        if (modifiers is not (ModifierKeys.None or ModifierKeys.Shift))
        {
            return;
        }

        if (Array.IndexOf(NavigationKeys, key) < 0)
        {
            return;
        }

        if (ForwardNavigationKeys?.Invoke() == false)
        {
            return;
        }

        // msg.hwnd is the focused window. If it is the top-level window rather than a
        // hosted child, no terminal has focus and WPF should handle the key normally.
        if (msg.hwnd == _windowHandle || msg.hwnd == IntPtr.Zero)
        {
            return;
        }

        SendMessageW(msg.hwnd, msg.message, msg.wParam, msg.lParam);
        handled = true;
    }

    /// <summary>
    /// Reads modifier state straight from Win32.
    /// <para>
    /// <see cref="Keyboard.Modifiers"/> is derived from WPF's input stack, which never
    /// sees these keystrokes because the terminal's child HWND consumes them. Relying on
    /// it makes Shift invisible, collapsing Ctrl+Shift+X into Ctrl+X.
    /// </para>
    /// </summary>
    private static ModifierKeys CurrentModifiers()
    {
        static bool Down(int key) => (GetKeyState(key) & 0x8000) != 0;

        var modifiers = ModifierKeys.None;

        if (Down(VkControl))
        {
            modifiers |= ModifierKeys.Control;
        }

        if (Down(VkShift))
        {
            modifiers |= ModifierKeys.Shift;
        }

        if (Down(VkMenu))
        {
            modifiers |= ModifierKeys.Alt;
        }

        return modifiers;
    }

    private readonly record struct Binding(Key Key, ModifierKeys Modifiers, Func<bool> Handler);

    /// <summary>
    /// Set <c>OVERSHELL_TRACE_KEYS=1</c> to log every chord the router observes to
    /// <c>%TEMP%\overshell-keys.log</c>. Off by default; useful when a shortcut misbehaves.
    /// </summary>
    private static readonly bool TraceEnabled =
        Environment.GetEnvironmentVariable("OVERSHELL_TRACE_KEYS") == "1";

    private static void Trace(int virtualKey, Key key, ModifierKeys modifiers)
    {
        if (!TraceEnabled)
        {
            return;
        }

        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overshell-keys.log"),
                $"{DateTime.Now:HH:mm:ss.fff}  vk=0x{virtualKey:X2} key={key,-12} mods={modifiers}{Environment.NewLine}");
        }
        catch
        {
            // Tracing must never break input handling.
        }
    }
}
