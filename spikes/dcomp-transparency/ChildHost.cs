using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using RawRect = Vortice.RawRect;

namespace DcompSpike;

/// <summary>
/// A child HWND hosted the way Microsoft.Terminal.Wpf hosts the terminal, drawing through
/// DirectX in one of three ways:
///   dcomp   - DirectComposition target bound to the child + composition swapchain, premultiplied alpha
///   hwnd    - swapchain created for the child HWND (what HwndTerminal does today; alpha ignored)
///   layered - the hwnd swapchain plus WS_EX_LAYERED + SetLayeredWindowAttributes(alpha) on the child
/// Every mode renders the same frame: a 45 % black background, opaque white "text" bars, an opaque orange block.
/// </summary>
public sealed class ChildHost : HwndHost
{
    private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipSiblings = 0x04000000, WsClipChildren = 0x02000000;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const int GwlExStyle = -20;
    private const uint LwaAlpha = 0x2;
    private const int WmEraseBkgnd = 0x0014, WmPaint = 0x000F;

    private static readonly ChildWndProc ProcKeepAlive = StaticWndProc;
    private static ushort s_atom;

    private delegate nint ChildWndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public nint lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(int exStyle, string className, string? windowName, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ValidateRect(nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(string? name);

    private nint _hwnd;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private IDCompositionDevice? _dcomp;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _visual;
    private DispatcherTimer? _timer;
    private int _width = Frame.Width, _height = Frame.Height;

    public event Action<string>? StatusChanged;

    public nint Hwnd => _hwnd;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        var instance = GetModuleHandleW(null);
        if (s_atom == 0)
        {
            var wc = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ProcKeepAlive),
                hInstance = instance,
                hbrBackground = 0,
                lpszClassName = "DcompSpikeChild",
            };
            s_atom = RegisterClassExW(ref wc);
            MainWindow.Log($"RegisterClassEx atom={s_atom} err={Marshal.GetLastWin32Error()}");
        }

        var exStyle = MainWindow.Mode.Contains("noredir") ? WsExNoRedirectionBitmap : MainWindow.Mode.Contains("layered") ? WsExLayered : 0;
        if (MainWindow.Mode.Contains("inputonly-redir")) { exStyle = 0; }
        _hwnd = CreateWindowExW(exStyle, "DcompSpikeChild", null, WsChild | WsVisible | WsClipSiblings | WsClipChildren, 0, 0, _width, _height, parent.Handle, 0, instance, 0);
        MainWindow.Log($"child hwnd=0x{_hwnd:X} err={Marshal.GetLastWin32Error()} exStyle=0x{exStyle:X}");

        if (MainWindow.Mode.Contains("inputonly"))
        {
            // Spike 8b: the child exists for input, TSF and UIA only; the parent window composes the
            // frame. Nothing is drawn here. Is the child still hit-testable, and does the parent's
            // visual blend with the backdrop where the child sits?
            StatusChanged?.Invoke("inputonly: non-redirected child with no graphics; the parent composes the frame at its rect");
            return new HandleRef(this, _hwnd);
        }

        try
        {
            SetUpGraphics();
            Render();
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => Render();
            _timer.Start();
        }
        catch (Exception e)
        {
            MainWindow.Log($"graphics setup FAILED: {e}");
            StatusChanged?.Invoke($"FAILED: {e.GetType().Name}: {e.Message}");
        }

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _timer?.Stop();
        DestroyWindow(hwnd.Handle);
    }

    private void SetUpGraphics()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        _device = device;
        _context = context;

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var mode = MainWindow.Mode;
        if (mode.Contains("dcomp"))
        {
            var description = new SwapChainDescription1
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
            };
            _swapChain = factory.CreateSwapChainForComposition(device, description);
            MainWindow.Log("composition swapchain created (premultiplied alpha)");

            _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            _dcomp.CreateTargetForHwnd(_hwnd, true, out _target).CheckError();
            MainWindow.Log($"DComp CreateTargetForHwnd on the CHILD hwnd: ok");
            _visual = _dcomp.CreateVisual();
            _visual.SetContent(_swapChain);
            _target.SetRoot(_visual);
            _dcomp.Commit();
            MainWindow.Log("DComp visual committed");
            StatusChanged?.Invoke("dcomp: composition swapchain, premultiplied alpha, DComp target on the child HWND");
        }
        else
        {
            var description = new SwapChainDescription1
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.None,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
            };
            _swapChain = factory.CreateSwapChainForHwnd(device, _hwnd, description);
            MainWindow.Log("hwnd swapchain created (alpha ignored, as the terminal control does)");

            if (mode.Contains("layered"))
            {
                var ok = SetLayeredWindowAttributes(_hwnd, 0, 128, LwaAlpha);
                MainWindow.Log($"SetLayeredWindowAttributes(alpha 128) on the child: {ok} err={Marshal.GetLastWin32Error()} exstyle now=0x{GetWindowLongPtrW(_hwnd, GwlExStyle):X}");
                StatusChanged?.Invoke("layered: hwnd swapchain + WS_EX_LAYERED alpha 128 on the child");
            }
            else
            {
                StatusChanged?.Invoke("hwnd: swapchain for the child HWND (today's terminal path)");
            }
        }
    }

    private void Render()
    {
        if (_swapChain is null || _device is null || _context is null)
        {
            return;
        }

        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        using var rtv = _device.CreateRenderTargetView(back);

        // 45 % black background, premultiplied: (0,0,0,0.45).
        _context.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 0.45f));

        // "Text": opaque white bars, and an opaque orange block, via ClearView with rectangles.
        using var context1 = _context.QueryInterface<ID3D11DeviceContext1>();
        var rects = new RawRect[]
        {
            new(40, 40, 520, 64),
            new(40, 90, 380, 114),
            new(40, 140, 600, 164),
            new(40, 190, 300, 214),
        };
        context1.ClearView(rtv, new Color4(1f, 1f, 1f, 1f), rects);
        context1.ClearView(rtv, new Color4(1f, 0.55f, 0f, 1f), [new RawRect(560, 280, 760, 420)]);

        // A fully transparent hole, to show what is behind the child without any tint.
        context1.ClearView(rtv, new Color4(0f, 0f, 0f, 0f), [new RawRect(40, 280, 400, 420)]);

        _swapChain.Present(1, PresentFlags.None);
    }

    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WmEraseBkgnd:
                return 1;
            case WmPaint:
                ValidateRect(hwnd, 0);
                return 0;
            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
