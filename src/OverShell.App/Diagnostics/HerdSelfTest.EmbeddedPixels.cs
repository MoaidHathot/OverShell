using System.Windows;
using System.Windows.Media;
using OverShell.App.Terminal.WindowsTerminal;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunEmbeddedPixelsAsync(MainWindow window, TerminalTab background)
    {
        Log("=== selftest (embeddedpixels) start ===");
        var pass = true;
        void Check(bool ok, string description) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {description}"); }
        var foreground = window.AddTab(background.Profile, activate: false);
        try
        {
            await WaitForPromptAsync(background);
            await WaitForPromptAsync(foreground);
            background.SendText(PreviewFillCommand(0, 0, 200));
            foreground.SendText(PreviewFillCommand(0, 200, 0));
            Check(await TransferUntilAsync(() => NativeTerminalPreview.Capture(background.TerminalHwnd)?.Runs.Any(r => (r.Background & 0xffffff) == 0xc80000) == true
                && NativeTerminalPreview.Capture(foreground.TerminalHwnd)?.Runs.Any(r => (r.Background & 0xffffff) == 0x00c800) == true), "both terminal buffers contain their distinct colour fixtures");
            window.ActiveTab = background;
            var windows = Application.Current.Windows.Count;
            window.Presentations.ShowFloating(foreground.Id);
            var panel = window.FloatingPanels.Single();
            Check(Application.Current.Windows.Count == windows && Window.GetWindow(foreground.View) == window, "the rendered float uses only the existing top-level window");
            if (OnInputDesktop && await BringToFrontAsync(window))
            {
                bool ForegroundGreen()
                {
                    GetWindowRect(foreground.TerminalHwnd, out var rect);
                    var c = ScreenPixel((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
                    return c.G > 150 && c.R < 50 && c.B < 50;
                }
                Check(await TransferUntilAsync(ForegroundGreen), "PIXELS: the embedded native terminal renders above the WPF background");
                var captionPoint = panel.CaptionSurface.PointToScreen(new Point(panel.CaptionSurface.ActualWidth / 2, 4));
                var expected = ((SolidColorBrush)window.FindResource("Surface.Chrome")).Color;
                Check(Distance(ScreenPixel((int)captionPoint.X, (int)captionPoint.Y), expected) < 35, "PIXELS: the terminal cannot cover its in-window caption");
                var backdropPoint = panel.PointToScreen(new Point(Math.Max(20, panel.Bounds.Left / 2), panel.ActualHeight / 2));
                Check(await TransferUntilAsync(() => { var c = ScreenPixel((int)backdropPoint.X, (int)backdropPoint.Y); return c.B > 100 && c.R < 60 && c.G < 60; }),
                    "PIXELS: the original terminal remains visible around the inset panel");
                window.Width -= 120;
                await Task.Delay(250);
                Check(new Rect(panel.RenderSize).Contains(panel.Bounds) && ForegroundGreen(), "PIXELS: resizing the owner keeps the float inside and rendering");
                SaveScreen(window, "overshell-embedded-floating.png");
                window.Presentations.HideFloating(foreground.Id);
                Check(await TransferUntilAsync(() =>
                {
                    GetWindowRect(background.TerminalHwnd, out var rect);
                    var c = ScreenPixel((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
                    return c.B > 150 && c.R < 50 && c.G < 50;
                }), "PIXELS: Hide restores the original native terminal without a stale overlay");
            }
            else Log("  SKIP  native in-window pixel checks require the input desktop");
        }
        finally
        {
            window.Presentations.HideFloating(foreground.Id);
            window.CloseTab(foreground);
        }
        Log($"=== selftest (embeddedpixels) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }
}
