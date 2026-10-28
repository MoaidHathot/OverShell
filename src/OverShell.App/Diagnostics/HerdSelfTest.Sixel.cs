using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using OverShell.App.Terminal.WindowsTerminal;
using OverShell.Core;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunSixelAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (sixel) start ===");
        var pass = true;
        void Check(bool ok, string description) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {description}"); }
        var topmost = window.Topmost;
        var fixture = Path.Combine(AppPaths.StateRoot, "sixel-fixture.vt");
        var drawings = 0;
        try
        {
            window.Width = 1180;
            window.Height = 760;
            window.Commands.TryExecute("view.terminal");
            Check(await TransferUntilAsync(() => tab.IsRunning && tab.TerminalHwnd != 0 && tab.Grid.Rows >= 25), "the real terminal is ready for Sixel output");
            await Task.Delay(500);
            await DrawFixture("Sixel images and all 256 palette entries pass through ConPTY");
            await CheckPixels();
            window.Width = 980;
            await Task.Delay(300);
            await DrawFixture("Sixel rendering remains usable after a terminal resize");
            await CheckPixels();

            // Optional real-world reproduction. The default checks are offline; a caller can
            // opt into a bounded command such as dnx MoaidHathot -- --duration 6.
            if (Environment.GetEnvironmentVariable("OVERSHELL_SIXEL_APP") is { Length: > 0 } command)
            {
                var result = Path.Combine(AppPaths.StateRoot, "sixel-app-exit.txt");
                var script = Path.Combine(AppPaths.StateRoot, "sixel-app.ps1");
                File.Delete(result);
                File.WriteAllText(script, command + $"\n[IO.File]::WriteAllText('{Ps(result)}', [string]$LASTEXITCODE)\n", new UTF8Encoding(false));
                Log($"  application: {command}");
                tab.SendText($"& '{Ps(script)}'\r");
                var clock = Stopwatch.StartNew();
                while (!File.Exists(result) && clock.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(100);
                var exit = File.Exists(result) ? File.ReadAllText(result) : "timeout";
                Check(exit == "0", $"the real Sixel application exits successfully (exit={exit})");
                if (exit == "timeout") tab.SendText("\x03");
                await DrawFixture("the shell and image renderer remain usable after the application exits");
                await CheckPixels();
            }
            Check(tab.IsRunning, "the shell remains running after Sixel images");
        }
        finally
        {
            window.Topmost = topmost;
            if (_previewBenchSettings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllText(AppPaths.SettingsFile, _previewBenchSettings);
            Environment.SetEnvironmentVariable("OVERSHELL_WT_SETTINGS", _previewBenchWt);
        }
        Log($"=== selftest (sixel) result: {(pass ? "ALL PASS" : "FAILED")} ===");

        async Task DrawFixture(string description)
        {
            // The marker is in the payload file, not in the echoed shell command. Seeing it
            // in the native buffer proves that parsing continued beyond the Sixel sequences.
            var marker = $"SIXEL-OUTPUT-COMPLETE-{++drawings}";
            File.WriteAllText(fixture, SixelFixture(marker), new UTF8Encoding(false));
            tab.SendText($"[Console]::Write([IO.File]::ReadAllText('{Ps(fixture)}'))\r");
            Check(await TransferUntilAsync(() => NativeTerminalPreview.Capture(tab.TerminalHwnd)?.Runs.Any(r => r.Text.Contains(marker, StringComparison.Ordinal)) == true), description);
        }

        async Task CheckPixels()
        {
            if (!OnInputDesktop)
            {
                Log("  SKIP  Sixel pixel checks require the input desktop (-Visible)");
                return;
            }
            window.Topmost = true;
            await Task.Delay(200);
            var frame = NativeTerminalPreview.Capture(tab.TerminalHwnd)!;
            Point At(double column, double row)
            {
                var point = new SixelPoint
                {
                    X = (int)(column * frame.PixelWidth / frame.Columns),
                    Y = (int)(row * frame.PixelHeight / frame.Rows),
                };
                if (!SixelClientToScreen(tab.TerminalHwnd, ref point)) throw new InvalidOperationException("Could not locate the terminal's Sixel pixels");
                return new Point(point.X, point.Y);
            }
            var red = At(5, 4);
            var blue = At(13, 4);
            if (!await TransferUntilAsync(() => PreviewOwnsPixel(window, red) && PreviewOwnsPixel(window, blue)))
            {
                Log("  SKIP  another window obscures the Sixel pixel checks");
                return;
            }
            Check(await TransferUntilAsync(() => { var c = ScreenPixel((int)red.X, (int)red.Y); return c.R > 220 && c.G < 40 && c.B < 40; }), "PIXELS: Sixel palette entry 0 renders red");
            Check(await TransferUntilAsync(() => { var c = ScreenPixel((int)blue.X, (int)blue.Y); return c.B > 220 && c.R < 40 && c.G < 40; }), "PIXELS: Sixel palette entry 255 renders blue");
            SaveScreen(window, "overshell-sixel.png");
        }

        static string Ps(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string SixelFixture(string marker)
    {
        var output = new StringBuilder("\x1b[0m\x1b[2J\x1b[H\x1b[?80l\x1b[4;4H");
        output.Append(Tile(0, "100;0;0")).Append("\x1b[4;12H").Append(Tile(255, "0;0;100"));
        output.Append("\x1b[12;2H\x1bP0;1q\"1;1;256;6");
        for (var index = 0; index < 256; index++) output.Append($"#{index};2;{index % 101};{(index * 3) % 101};{(index * 7) % 101}");
        for (var index = 0; index < 256; index++) output.Append($"#{index}~");
        return output.Append("\x1b\\\x1b[20;1H").Append(marker).Append("\r\n").ToString();

        static string Tile(int index, string rgb) => $"\x1bP0;1q\"1;1;40;40#{index};2;{rgb}!40~-!40~-!40~-!40~-!40~-!40~-!40~\x1b\\";
    }

    [StructLayout(LayoutKind.Sequential)] private struct SixelPoint { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "ClientToScreen")] private static extern bool SixelClientToScreen(nint window, ref SixelPoint point);
}
