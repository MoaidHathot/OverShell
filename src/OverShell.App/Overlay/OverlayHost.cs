using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using OverShell.App.Terminal;

namespace OverShell.App.Overlay;

/// <summary>
/// Draws over the terminal body — which WPF itself cannot do, because the terminal is a
/// native child HWND with its own swapchain (airspace, DESIGN.md §9).
/// <para>
/// The trick is the one every WebView2-in-WPF application uses: a separate top-level
/// window, owned by the main window so it stays above it, layered with per-pixel alpha,
/// <c>WS_EX_TRANSPARENT</c> so the mouse falls straight through to the terminal, and
/// <c>WS_EX_NOACTIVATE</c> so it can never take focus from the shell. It is sized to what
/// it draws, not to the terminal, so the layered bitmap stays a few hundred bytes.
/// </para>
/// <para>
/// Everything is positioned in physical pixels through <c>SetWindowPos</c> — Window.Left
/// and friends are DIPs whose meaning depends on which monitor the window is on, and this
/// window moves between monitors with the terminal.
/// </para>
/// </summary>
internal sealed class OverlayHost : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    // DWMWA_WINDOW_CORNER_PREFERENCE / DWMWCP_DONOTROUND: Windows 11 must not round the
    // corners of a two-pixel-high window.
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;

    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly nint _hwnd;
    private Rect _placed = Rect.Empty;

    public OverlayHost(Window owner)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Content = _canvas;

        // Create the HWND now, so the first reveal can be placed before it is shown.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();

        var style = GetWindowLongPtrW(_hwnd, GwlExStyle).ToInt64();
        _ = SetWindowLongPtrW(_hwnd, GwlExStyle, (nint)(style | WsExTransparent | WsExToolWindow | WsExNoActivate));

        var corner = DwmwcpDoNotRound;
        _ = DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Win32Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Underlines the given cells. Rectangles are physical screen pixels, one per run of
    /// cells on a row. With <paramref name="underline"/> the line sits exactly where the
    /// terminal's own renderer would put one; without, a little above the cell's bottom.
    /// </summary>
    public void ShowUnderline(IReadOnlyList<Rect> cellRects, Brush brush, TerminalUnderline? underline)
    {
        if (cellRects.Count == 0)
        {
            HideOverlay();
            return;
        }

        var scale = VisualTreeHelper.GetDpi(Owner).DpiScaleX;

        // Fallback: whole pixels scaled with DPI, a hair above the cell edge so it never
        // bleeds into the next row.
        var fallbackThickness = Math.Max(1, (int)Math.Round(scale));
        var fallbackGap = fallbackThickness;

        var lines = new List<Rect>(cellRects.Count);
        foreach (var cell in cellRects)
        {
            var x = Math.Round(cell.X);
            var right = Math.Round(cell.Right);

            double y;
            int thickness;
            if (underline is { } u && u.Thickness > 0 && u.Top + u.Thickness <= Math.Round(cell.Height))
            {
                y = Math.Round(cell.Y) + u.Top;
                thickness = u.Thickness;
            }
            else
            {
                thickness = fallbackThickness;
                y = Math.Floor(cell.Bottom) - fallbackGap - thickness;
            }

            lines.Add(new Rect(x, y, Math.Max(1, right - x), thickness));
        }

        var union = lines[0];
        for (var i = 1; i < lines.Count; i++)
        {
            union.Union(lines[i]);
        }

        Place(union);

        // Content is laid out in DIPs relative to the window; read the DPI back after
        // placement, in case landing on this monitor changed it.
        scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        _canvas.Children.Clear();
        foreach (var line in lines)
        {
            var bar = new Rectangle
            {
                Fill = brush,
                Width = line.Width / scale,
                Height = line.Height / scale,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(bar, (line.X - union.X) / scale);
            Canvas.SetTop(bar, (line.Y - union.Y) / scale);
            _canvas.Children.Add(bar);
        }

        if (!IsVisible)
        {
            Show();
        }
    }

    public void HideOverlay()
    {
        _canvas.Children.Clear();
        _placed = Rect.Empty;

        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>Window rectangle as the OS sees it, physical pixels. Diagnostics only.</summary>
    internal string Describe()
    {
        _ = GetWindowRect(_hwnd, out var r);
        var requested = _placed.IsEmpty ? "none" : $"[{_placed.X},{_placed.Y} {_placed.Width}x{_placed.Height}]";
        return $"hwnd=0x{_hwnd:X} visible={IsVisible} rect=[{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}] " +
               $"requested={requested} actual={ActualWidth:F1}x{ActualHeight:F1} dip children={_canvas.Children.Count}";
    }

    /// <summary>
    /// Moves and sizes the HWND in physical pixels. Crossing onto a monitor with a
    /// different DPI makes WPF re-apply the OS-suggested size; the second pass, now on the
    /// right monitor, sticks.
    /// </summary>
    private void Place(Rect physical)
    {
        if (physical == _placed)
        {
            return;
        }

        _placed = physical;

        var x = (int)Math.Floor(physical.X);
        var y = (int)Math.Floor(physical.Y);
        var width = Math.Max(1, (int)Math.Ceiling(physical.Width));
        var height = Math.Max(1, (int)Math.Ceiling(physical.Height));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            _ = SetWindowPos(_hwnd, 0, x, y, width, height, SwpNoActivate | SwpNoZOrder | SwpNoOwnerZOrder);

            if (GetWindowRect(_hwnd, out var actual) &&
                actual.Left == x && actual.Top == y &&
                actual.Right - actual.Left == width && actual.Bottom - actual.Top == height)
            {
                return;
            }
        }
    }
}
