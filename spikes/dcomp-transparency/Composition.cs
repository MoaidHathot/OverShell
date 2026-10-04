using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using RawRect = Vortice.RawRect;

namespace DcompSpike;

/// <summary>
/// The frame every mode draws into an 800x450 swapchain: a 45 % black background, opaque
/// white "text" bars, an opaque orange block, and a fully transparent hole.
/// </summary>
public static class Frame
{
    public const int Width = 800;
    public const int Height = 450;

    public static (ID3D11Device Device, ID3D11DeviceContext Context) CreateDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        return (device, context);
    }

    public static SwapChainDescription1 CompositionDescription() => new()
    {
        Width = Width,
        Height = Height,
        Format = Format.B8G8R8A8_UNorm,
        Stereo = false,
        SampleDescription = new SampleDescription(1, 0),
        BufferUsage = Usage.RenderTargetOutput,
        BufferCount = 2,
        Scaling = Scaling.Stretch,
        SwapEffect = SwapEffect.FlipSequential,
        AlphaMode = AlphaMode.Premultiplied,
    };

    public static void Render(ID3D11Device device, ID3D11DeviceContext context, IDXGISwapChain1 swapChain)
    {
        using var back = swapChain.GetBuffer<ID3D11Texture2D>(0);
        using var rtv = device.CreateRenderTargetView(back);
        context.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 0.45f));

        using var context1 = context.QueryInterface<ID3D11DeviceContext1>();
        context1.ClearView(rtv, new Color4(1f, 1f, 1f, 1f), [new RawRect(40, 40, 520, 64), new RawRect(40, 90, 380, 114), new RawRect(40, 140, 600, 164), new RawRect(40, 190, 300, 214)]);
        context1.ClearView(rtv, new Color4(1f, 0.55f, 0f, 1f), [new RawRect(560, 280, 760, 420)]);
        context1.ClearView(rtv, new Color4(0f, 0f, 0f, 0f), [new RawRect(40, 280, 400, 420)]);
        swapChain.Present(1, PresentFlags.None);
    }
}

/// <summary>
/// No child HWND: a DirectComposition target bound to the top-level (WPF) window, with the
/// content visual placed where the terminal would be. topmost=false puts the visual under
/// the window's redirection surface - WPF's transparent pixels reveal it and WPF's chrome
/// draws over it (no airspace problem); topmost=true puts it over everything.
/// </summary>
public sealed class ParentComposer
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDCompositionDevice _dcomp;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;

    public ParentComposer(nint topLevelHwnd, bool topmost)
    {
        (_device, _context) = Frame.CreateDevice();
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _swapChain = factory.CreateSwapChainForComposition(_device, Frame.CompositionDescription());
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _dcomp.CreateTargetForHwnd(topLevelHwnd, topmost, out _target).CheckError();
        _visual = _dcomp.CreateVisual();
        _visual.SetContent(_swapChain);
        _target.SetRoot(_visual);
        _dcomp.Commit();
        Frame.Render(_device, _context, _swapChain);
        MainWindow.Log($"parent composer: DComp target on the TOP-LEVEL hwnd 0x{topLevelHwnd:X}, topmost={topmost}: ok");
    }

    public void Place(float x, float y)
    {
        _visual.SetOffsetX(x);
        _visual.SetOffsetY(y);
        _dcomp.Commit();
    }
}
