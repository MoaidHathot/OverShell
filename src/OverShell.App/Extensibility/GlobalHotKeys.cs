using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OverShell.Core.Input;

namespace OverShell.App.Extensibility;

/// <summary>
/// System-wide hotkeys for extensions (DESIGN.md §12.16): <c>RegisterHotKey</c> on the main
/// window's HWND, <c>WM_HOTKEY</c> dispatched to the owner's callback. A hotkey another
/// program holds fails to register and says so; nothing else is touched. Being the window
/// that receives the hotkey is what entitles us to take the foreground afterwards.
/// </summary>
internal sealed class GlobalHotKeys : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    private readonly Window _window;
    private readonly Dictionary<int, Action> _handlers = [];
    private HwndSource? _source;
    private int _nextId = 0x5A00;

    public GlobalHotKeys(Window window)
    {
        _window = window;
    }

    /// <summary>Registers a chord; the id to unregister with, or null with a reason when Windows refused.</summary>
    public (int? Id, string? Error) Register(KeyChord chord, Action onPressed)
    {
        if (!Enum.TryParse<System.Windows.Input.Key>(chord.Key, ignoreCase: true, out var key))
        {
            return (null, $"'{chord}' names no key");
        }

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == 0)
        {
            return (null, "the window has no handle yet");
        }

        if (_source is null)
        {
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(Hook);
        }

        var modifiers = ModNoRepeat;
        if (chord.Modifiers.HasFlag(ChordModifiers.Control)) modifiers |= ModControl;
        if (chord.Modifiers.HasFlag(ChordModifiers.Shift)) modifiers |= ModShift;
        if (chord.Modifiers.HasFlag(ChordModifiers.Alt)) modifiers |= ModAlt;
        if (chord.Modifiers.HasFlag(ChordModifiers.Win)) modifiers |= ModWin;

        var id = _nextId++;
        if (!RegisterHotKey(hwnd, id, modifiers, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key)))
        {
            var error = Marshal.GetLastWin32Error();
            return (null, error == 1409 ? $"'{chord}' is taken by another program" : $"RegisterHotKey failed ({error})");
        }

        _handlers[id] = onPressed;
        return (id, null);
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id))
        {
            _ = UnregisterHotKey(new WindowInteropHelper(_window).Handle, id);
        }
    }

    /// <summary>Runs the handler registered under <paramref name="id"/> - what WM_HOTKEY does; for diagnostics.</summary>
    internal bool Fire(int id)
    {
        if (!_handlers.TryGetValue(id, out var handler))
        {
            return false;
        }

        handler();
        return true;
    }

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotKey && _handlers.TryGetValue((int)wParam, out var handler))
        {
            handler();
            handled = true;
        }

        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys.ToList())
        {
            Unregister(id);
        }

        _source?.RemoveHook(Hook);
    }
}
