using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OverShell.Core.Settings;

namespace OverShell.App.Chrome;

internal static class FloatingPlacement
{
    internal static Rect Calculate(Rect work, Rect owner, FloatingSettings settings)
    {
        var width = Math.Min(work.Width, Math.Max(400, owner.Width * settings.EffectiveWidth));
        var height = Math.Min(work.Height, Math.Max(240, owner.Height * settings.EffectiveHeight));
        var right = settings.Position is "topRight" or "bottomRight";
        var x = right ? owner.Right - width - 16 : owner.Left + (owner.Width - width) / 2;
        var y = settings.Position == "topRight" ? owner.Top + 48
            : settings.Position == "bottomRight" ? owner.Bottom - height - 16 : owner.Top + (owner.Height - height) / 2;
        return new Rect(Math.Clamp(x, work.Left, work.Right - width), Math.Clamp(y, work.Top, work.Bottom - height), width, height);
    }

    internal static void Apply(Window window, Window owner, FloatingSettings settings)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info) || !GetWindowRect(hwnd, out var bounds)) return;
        var placed = Calculate(info.Work.Rect, bounds.Rect, settings);
        _ = SetWindowPos(new WindowInteropHelper(window).Handle, 0, (int)placed.X, (int)placed.Y, (int)placed.Width, (int)placed.Height, 0x0004 | 0x0010);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect Rect => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
