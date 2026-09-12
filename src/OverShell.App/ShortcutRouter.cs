using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace OverShell.App;

/// <summary>
/// Application shortcut routing that works over a hosted native terminal.
/// <para>
/// The Windows Terminal control owns its own child HWND and forwards every
/// <c>WM_KEYDOWN</c> straight into the terminal, so WPF <see cref="InputBinding"/>s on
/// the window never fire — the shell swallows Ctrl+T before WPF sees it. Hooking
/// <see cref="ComponentDispatcher.ThreadPreprocessMessage"/> lets us claim a small set
/// of reserved chords before <c>DispatchMessage</c> hands them to the terminal.
/// </para>
/// </summary>
internal sealed class ShortcutRouter : IDisposable
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmRButtonUp = 0x0205;
    private const uint GaRoot = 2;

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

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

        var isKey = msg.message is WmKeyDown or WmSysKeyDown;
        if (!isKey && msg.message != WmRButtonUp)
        {
            return;
        }

        // Only ever act on input genuinely destined for this window.
        if (_windowHandle == IntPtr.Zero || GetAncestor(msg.hwnd, GaRoot) != _windowHandle)
        {
            return;
        }

        if (!isKey)
        {
            if (RightClick?.Invoke(new Point(msg.pt_x, msg.pt_y)) == true)
            {
                handled = true;
            }

            return;
        }

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
