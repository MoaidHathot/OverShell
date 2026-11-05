using System.Windows;
using System.Windows.Media;
using OverShell.App.Terminal.WindowsTerminal;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunFloatingStyleAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (floatstyle) start ===");
        var pass = true;
        void Check(bool ok, string description) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {description}"); }
        var second = window.AddTab(first.Profile, activate: false);
        var leases = NativeTerminalPreview.ActiveLeases;
        var topmost = window.Topmost;
        var pixels = false;
        try
        {
            await WaitForPromptAsync(first);
            await WaitForPromptAsync(second);
            window.ActiveTab = first;
            first.Surface.Focus();
            var size = first.FontSize;
            var opacity = first.Surface.BackgroundOpacity;
            var before = NativeTerminalPreview.Capture(first.TerminalHwnd)!;
            var height = await NativeCellHeightAsync(first.TerminalHwnd);
            Point Center() => window.TerminalArea.PointToScreen(new Point(window.TerminalArea.ActualWidth / 2, window.TerminalArea.ActualHeight / 2));
            Point Edge() => window.TerminalArea.PointToScreen(new Point(24, window.TerminalArea.ActualHeight / 2));
            if (OnInputDesktop)
            {
                // Visibility is sufficient for pixel checks; no foreground activation is needed.
                window.Topmost = true;
                pixels = await TransferUntilAsync(() => PreviewOwnsPixel(window, Center()) && PreviewOwnsPixel(window, Edge()));
            }
            // A tiling manager may reposition the test window when its style changes.
            var center = Center();
            var edge = Edge();
            var centerBefore = pixels ? ScreenPixel((int)center.X, (int)center.Y) : default;
            var edgeBefore = pixels ? ScreenPixel((int)edge.X, (int)edge.Y) : default;
            var expectedBounds = Chrome.FloatingPlacement.Calculate(window.TerminalArea.RenderSize, window.CurrentSettings.Floating);
            var selfSeams = CaptureSeams(expectedBounds);
            var otherSeams = CaptureSeams(new Rect(expectedBounds.Location, new Size(expectedBounds.Width - 100, expectedBounds.Height - 60)));

            Check(window.Presentations.ShowFloating(first.Id).Success, "the current terminal opens in an embedded panel");
            var self = window.FloatingPanels.Single();
            Check(!self.HasBackgroundPreview && self.Backdrop.Backend == "tint", "floating the current tab does not render a duplicate terminal behind itself");
            Check(first.FontSize == size && Math.Abs(first.Surface.BackgroundOpacity - opacity) < 0.001, "floating preserves the terminal's font size and configured opacity");
            var afterHeight = await NativeCellHeightAsync(first.TerminalHwnd);
            Log($"  native font: {height}->{afterHeight}; points={first.FontSize}; grids {before.Columns}x{before.Rows}->{first.Grid}");
            Check(height is not null && afterHeight == height, "the native text-provider cell height stays unchanged when the terminal is floated");
            Check(self.Bounds.Width <= window.CurrentSettings.Floating.EffectiveMaxWidth && self.Bounds.Height <= window.CurrentSettings.Floating.EffectiveMaxHeight,
                "large windows respect the configured comfortable panel size limits");
            if (pixels)
            {
                await Task.Delay(350);
                var centerAfter = ScreenPixel((int)center.X, (int)center.Y);
                var edgeAfter = ScreenPixel((int)edge.X, (int)edge.Y);
                Log($"  alpha pixels: center {centerBefore}->{centerAfter}; background {edgeBefore}->{edgeAfter}; opacity={opacity}");
                Check(Distance(centerBefore, centerAfter) < 20, "PIXELS: the floating terminal's default background retains its transparency");
                Check(Distance(edgeBefore, edgeAfter) < 20, "PIXELS: the surrounding background keeps the original tint instead of becoming opaque");
                CheckSeams(selfSeams, "current tab");
                SaveScreen(window, "overshell-floating-style-current.png");
            }
            window.Presentations.HideFloating(first.Id);
            Check(first.FontSize == size && Math.Abs(first.Surface.BackgroundOpacity - opacity) < 0.001, "Hide restores the same appearance without changing the profile");
            Check(await TransferUntilAsync(() => first.Grid == (before.Columns, before.Rows)), "Hide restores the original native grid before the next presentation");

            var backgroundGrid = first.Grid;
            Check(window.Presentations.ShowFloating(second.Id).Success, "a different terminal opens over the original work");
            var panel = window.FloatingPanels.Single();
            Check(panel.HasBackgroundPreview && first.Grid == backgroundGrid, "a distinct background keeps its original grid");
            if (Terminal.TerminalFactory.Composition.Enabled)
                Check(await TransferUntilAsync(() => panel.Backdrop.Backend == "live"), "the composed background uses actual renderer pixels, including styles and transparency");
            GetWindowRect(first.TerminalHwnd, out var native);
            var dpi = VisualTreeHelper.GetDpi(window);
            Check(await TransferUntilAsync(() => Math.Abs(panel.Backdrop.Destination.Width - (native.Right - native.Left) / dpi.DpiScaleX) < 1),
                "background content stays at its native pixel scale");
            panel.ResizeBy(-100, -60);
            Check(first.Grid == backgroundGrid && first.FontSize == size, "resizing the inset cannot enlarge the background terminal text");
            if (pixels)
            {
                await Task.Delay(350);
                Check(Distance(edgeBefore, ScreenPixel((int)edge.X, (int)edge.Y)) < 20, "PIXELS: a different tab's background preserves its original alpha");
                CheckSeams(otherSeams, "different tab after resize");
                SaveScreen(window, "overshell-floating-style-other.png");
            }
            window.Presentations.HideFloating(second.Id);
            Check(NativeTerminalPreview.ActiveLeases == leases && Chrome.StyledPreviewSource.ActiveSources == 0, "hiding releases every live and styled background consumer");
        }
        finally
        {
            window.Topmost = topmost;
            window.Presentations.HideFloating(first.Id);
            window.Presentations.HideFloating(second.Id);
            window.CloseTab(second);
        }
        if (!pixels) Log("  SKIP  transparency pixel comparisons require a visible test window on an unlocked input desktop");
        Log($"=== selftest (floatstyle) result: {(pass ? "ALL PASS" : "FAILED")} ===");

        List<(Point Position, Color Before)> CaptureSeams(Rect bounds)
        {
            var samples = new List<(Point, Color)>();
            if (!pixels) return samples;
            foreach (var y in new[] { bounds.Top, bounds.Bottom })
            {
                var point = window.TerminalArea.PointToScreen(new Point(window.TerminalArea.ActualWidth - 24, y));
                for (var dy = -3; dy <= 3; dy++)
                {
                    var position = new Point(Math.Round(point.X), Math.Round(point.Y) + dy);
                    samples.Add((position, ScreenPixel((int)position.X, (int)position.Y)));
                }
            }
            return samples;
        }

        void CheckSeams(List<(Point Position, Color Before)> samples, string label)
        {
            var difference = samples.Max(sample => Distance(sample.Before, ScreenPixel((int)sample.Position.X, (int)sample.Position.Y)));
            Log($"  backdrop seam pixel difference ({label}): {difference}");
            Check(difference < 8, $"PIXELS: backdrop clip boundaries have no bright gaps or dark overlaps ({label})");
        }
    }

    private static Task<double?> NativeCellHeightAsync(nint hwnd) => Task.Run(() =>
    {
        var element = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
        var text = (System.Windows.Automation.TextPattern)element.GetCurrentPattern(System.Windows.Automation.TextPattern.Pattern);
        var prompt = text.DocumentRange.FindText("PS ", backward: true, ignoreCase: false);
        var rect = prompt?.GetBoundingRectangles().FirstOrDefault();
        return rect is { Height: > 0 } bounds ? bounds.Height : (double?)null;
    });
}
