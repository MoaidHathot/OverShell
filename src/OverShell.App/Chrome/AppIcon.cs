using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OverShell.App.Chrome;

/// <summary>
/// The application icon (Assets/OverShell.ico, rendered by build/Make-Icon.ps1) for the caption
/// bar (DESIGN.md §12.25). The .ico holds a frame per size (16 to 256 px); an <c>Image</c> fed
/// the file as a whole would take the first frame and scale it, which is soft at every DPI, so
/// the frame whose pixel size matches the device pixels is chosen and shown at that size exactly.
/// Read from the copy beside the exe, which ships anyway for the toasts' IconUri: the project
/// lists the file as Content, and WPF resolves a pack URI to a content file through the file
/// system (ignoring the item's Link), so an embedded copy would not be reachable by that name.
/// </summary>
internal static class AppIcon
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "OverShell.ico");
    private static IconBitmapDecoder? _decoder;

    /// <summary>
    /// Sizes <paramref name="image"/> to <paramref name="dip"/> device-independent pixels at the
    /// window's scale and gives it the icon frame of that pixel size (the nearest larger one when
    /// there is no exact match, so a scale between two frames downsamples rather than upsamples).
    /// </summary>
    public static void Apply(System.Windows.Controls.Image image, double dip)
    {
        var decoder = _decoder ??= Load();
        if (decoder is null)
        {
            return;
        }

        var scale = PresentationSource.FromVisual(image) is not null ? VisualTreeHelper.GetDpi(image).DpiScaleX : 1.0;
        var wantPx = Math.Max(1, (int)Math.Round(dip * scale));
        var frame = decoder.Frames
            .OrderBy(f => f.PixelWidth >= wantPx ? 0 : 1)
            .ThenBy(f => Math.Abs(f.PixelWidth - wantPx))
            .First();

        image.Source = frame;
        // Shown at the frame's own size in device pixels when it is the exact frame; otherwise at the
        // asked-for size, downsampled with the high-quality scaler set on the element in XAML.
        var sizeDip = frame.PixelWidth == wantPx ? frame.PixelWidth / scale : dip;
        image.Width = sizeDip;
        image.Height = sizeDip;
    }

    private static IconBitmapDecoder? Load()
    {
        try
        {
            return new IconBitmapDecoder(new Uri(Path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        catch (Exception e)
        {
            // No icon is better than no window: the caption simply stays empty there.
            Diagnostics.HerdSelfTest.Note($"caption icon not loaded: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }
}
