using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using OverShell.App.Terminal.WindowsTerminal;
using OverShell.Core;
using OverShell.Core.Previews;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunNativePreviewAsync(MainWindow window, TerminalTab source)
    {
        Log("=== selftest (previewnative) start ===");
        var pass = true;
        void Check(bool ok, string description) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {description}"); }
        var host = new Window
        {
            Title = "OverShell preview proof", Width = 480, Height = 300, Left = 80, Top = 80,
            ShowActivated = false, ShowInTaskbar = false, Background = Brushes.Black,
            Content = new Grid(), Topmost = OnInputDesktop, Owner = window,
            WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
        };
        NativeTerminalPreview.Lease? preview = null;
        TerminalTab? second = null;
        try
        {
            Check(NativeTerminalPreview.Available, "native preview and styled-snapshot ABI is present");
            if (!NativeTerminalPreview.Available) throw new InvalidOperationException("Preview-capable terminal DLL required");
            await Task.Delay(500);
            source.SendText("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)\r");
            source.SendText(PreviewFixtureCommand(1));
            TerminalPreviewFrame? frame = null;
            Check(await TransferUntilAsync(() => (frame = NativeTerminalPreview.Capture(source.TerminalHwnd))?.Runs.Any(r => r.Text.Contains("中文", StringComparison.Ordinal)) == true), "a coherent styled viewport can be captured after the fixture executes");
            File.WriteAllText(Path.Combine(AppPaths.StateRoot, "native-snapshot.json"), Jsonc.Serialize(frame));
            Check(frame!.Runs.Any(r => (r.Foreground & 0xffffff) == 0x5050ff && r.Text.Contains("RED", StringComparison.Ordinal)), "snapshot preserves true-colour foreground");
            Check(frame.Runs.Any(r => (r.Background & 0xffffff) == 0x8c501e), "snapshot preserves coloured backgrounds, including blank cells");
            Check(frame.Runs.Any(r => r.Style.HasFlag(PreviewTextStyle.Bold) && r.Text.Contains("Bold", StringComparison.Ordinal)) &&
                  frame.Runs.Any(r => r.Style.HasFlag(PreviewTextStyle.Italic) && r.Text.Contains("Italic", StringComparison.Ordinal)) &&
                  frame.Runs.Any(r => r.Style.HasFlag(PreviewTextStyle.Underline) && r.Text.Contains("Underline", StringComparison.Ordinal)), "snapshot carries bold, italic and underline styles");
            Check(frame.Runs.Any(r => r.Text.Contains("中文", StringComparison.Ordinal)), "snapshot preserves wide Unicode glyphs");
            var grid = source.Grid;
            Check(frame.Columns == grid.Columns && frame.Rows == grid.Rows, "snapshot dimensions match the terminal's real grid");
            second = window.AddTab(source.Profile, activate: true);
            await TransferUntilAsync(() => second.IsRunning);
            Check(source.View.Visibility == Visibility.Hidden, "the mirror source is a background, hidden terminal");
            host.Show();
            await Task.Delay(200);
            var handle = new WindowInteropHelper(host).Handle;
            if (Terminal.TerminalFactory.Composition.Enabled)
            {
                Check(await TransferUntilAsync(() => (preview ??= NativeTerminalPreview.Create(source.TerminalHwnd, handle, window.Dispatcher)) is not null), "a live composition mirror attaches to a second window");
                Check(preview!.Update(new Rect(20, 20, 400, 220), new Rect(20, 20, 400, 220), 6), "the mirror accepts aspect-fit geometry and clipping");
                Check(source.Grid == grid, "creating/scaling a mirror preserves source rows and columns");
                for (var i = 0; i < 10; i++) preview.Update(new Rect(20, 20, 200 + i * 10, 180), new Rect(20, 20, 400, 220), 6);
                Check(source.Grid == grid, "resizing the mirror repeatedly cannot reflow the PTY");
                var after = NativeTerminalPreview.Capture(source.TerminalHwnd)!;
                Check(after.Columns == frame.Columns && after.Rows == frame.Rows, "the native buffer also retains its original grid after mirroring");
                preview.Update(new Rect(20, 20, 400, 220), new Rect(20, 20, 400, 220), 6);

                if (OnInputDesktop)
                {
                    var origin = ((Grid)host.Content).PointToScreen(new Point());
                    var x = (int)origin.X + 220;
                    var y = (int)origin.Y + 130;
                    source.SendText(PreviewFillCommand(255, 0, 0));
                    Check(await TransferUntilAsync(() => { var c = ScreenPixel(x, y); return c.R > 220 && c.G < 40 && c.B < 40; }), "PIXELS: hidden source paints red in its live mirror");
                    var fit = PreviewFit.Calculate(after.PixelWidth, after.PixelHeight, 400, 220);
                    var edge = ScreenPixel((int)(origin.X + 20 + fit.Left + fit.Width * 0.9), y);
                    Check(edge.R > 220 && edge.G < 40, "PIXELS: the mirror fits the rendered surface width, including after host layout changes");
                    source.SendText(PreviewFillCommand(0, 255, 0));
                    Check(await TransferUntilAsync(() => { var c = ScreenPixel(x, y); return c.G > 220 && c.R < 40 && c.B < 40; }), "PIXELS: hidden source updates the mirror to green without activation");
                    Check(Distance(ScreenPixel((int)origin.X + 4, (int)origin.Y + 4), Colors.Black) < 10, "PIXELS: the mirror stays inside its destination clip");
                    SaveScreen(host, "overshell-preview-native.png");
                }
                else Log("  SKIP  native pixel comparison requires the input desktop");

                Check(preview.Update(new Rect(20, 20, 400, 220), new Rect(1000, 1000, 10, 10), 0), "a fully clipped mirror detaches cleanly");
                preview.Dispose();
                preview.Dispose();
                Check(NativeTerminalPreview.ActiveLeases == 0, "disposing a mirror twice releases its lease exactly once");
            }
            else
            {
                Check(NativeTerminalPreview.Create(source.TerminalHwnd, handle, window.Dispatcher) is null && NativeTerminalPreview.Capture(source.TerminalHwnd) is not null,
                    "opaque terminals provide styled snapshots when live composition is unavailable");
            }
            Check(source.Grid == grid && source.IsRunning, "the original terminal remains alive at its original dimensions");
        }
        finally
        {
            preview?.Dispose();
            host.Close();
            if (second is not null && window.Tabs.Contains(second)) window.CloseTab(second);
            if (_previewBenchSettings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllText(AppPaths.SettingsFile, _previewBenchSettings);
            Environment.SetEnvironmentVariable("OVERSHELL_WT_SETTINGS", _previewBenchWt);
        }
        Log($"=== selftest (previewnative) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    private static string PreviewFillCommand(int r, int g, int b) =>
        $"& {{ $e=[string][char]27; [Console]::Write($e+'[48;2;{r};{g};{b}m'+$e+'[2J'+$e+'[H') }}\r";
}
