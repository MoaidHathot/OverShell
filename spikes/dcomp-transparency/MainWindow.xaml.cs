using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DcompSpike;

public partial class MainWindow : Window
{
    public static string Mode = "child-dcomp";
    public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "opencode", "dcomp-spike.log");

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Win32Rect rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint src, int srcX, int srcY, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Win32Rect { public int Left, Top, Right, Bottom; }

    private ParentComposer? _parent;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"DcompSpike [{Mode}]";
        Caption.Text = $"WPF caption - mode {Mode} (translucent over the backdrop)";

        if (Mode.StartsWith("parent", StringComparison.Ordinal))
        {
            // No child HWND: the content is a DComp visual on the top-level window, under WPF.
            HostBorder.Child = null;
        }

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(hwnd)!;
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            var hrFrame = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            var dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            var backdrop = Mode.Contains("mica") ? 2 : 3;
            var hrBackdrop = DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
            Log($"[{Mode}] window hwnd=0x{hwnd:X} extend-frame hr=0x{hrFrame:X8} backdrop={backdrop} hr=0x{hrBackdrop:X8}");

            if (Mode.StartsWith("parent", StringComparison.Ordinal))
            {
                try
                {
                    _parent = new ParentComposer(hwnd, topmost: Mode.Contains("topmost"));
                    Status.Text = $"parent: DComp target on the top-level HWND, topmost={Mode.Contains("topmost")}, composition swapchain with alpha";
                }
                catch (Exception e)
                {
                    Log($"[{Mode}] parent composer FAILED: {e}");
                    Status.Text = $"FAILED: {e.Message}";
                }
            }
        };

        Host.StatusChanged += text => Dispatcher.BeginInvoke(() => Status.Text = text);

        Loaded += (_, _) =>
        {
            // Place the parent-mode visual where the host border is, in physical pixels.
            LayoutUpdated += (_, _) => PlaceParentVisual();
            PlaceParentVisual();

            Activate();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Activate();
                Log($"[{Mode}] IsActive={IsActive}");
                Capture();
                Application.Current.Shutdown();
            };
            timer.Start();
        };
    }

    private void PlaceParentVisual()
    {
        if (_parent is null || !HostBorder.IsLoaded)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var origin = HostBorder.TransformToAncestor(this).Transform(new Point(0, 0));
        _parent.Place((float)(origin.X * dpi) + 3, (float)(origin.Y * dpi) + 3);
    }

    private void Capture()
    {
        const uint SrcCopy = 0x00CC0020;
        var hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var rect);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, SrcCopy);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Crop to the content area the spike cares about: the host border region plus the WPF text.
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var cropHeight = Math.Min(height, (int)((48 + 24) * dpi) + 520);
            var cropped = new CroppedBitmap(source, new Int32Rect(0, 0, Math.Min(width, 900), cropHeight));

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            var path = Path.Combine(Path.GetTempPath(), "opencode", $"dcomp-{Mode}.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            Log($"[{Mode}] captured {path} ({width}x{height} at {rect.Left},{rect.Top}; dpi {dpi})");

            // Sample a few pixels of interest, physical coordinates relative to the host content origin.
            var origin = HostBorder.TransformToAncestor(this).Transform(new Point(0, 0));
            var ox = (int)(origin.X * dpi) + 3;
            var oy = (int)(origin.Y * dpi) + 3;
            string Sample(string what, int x, int y)
            {
                var px = new byte[4];
                source.CopyPixels(new Int32Rect(ox + x, oy + y, 1, 1), px, 4, 0);
                return $"{what}=#{px[2]:X2}{px[1]:X2}{px[0]:X2}";
            }

            Log($"[{Mode}] pixels: {Sample("bg45", 20, 20)} {Sample("whitebar", 100, 52)} {Sample("hole", 200, 350)} {Sample("orange", 660, 350)} {Sample("outside-child-right", 820, 20)} {Sample("wpf-text-row", 20, 480)}");
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
