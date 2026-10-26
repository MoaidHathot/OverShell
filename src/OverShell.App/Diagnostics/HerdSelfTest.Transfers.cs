using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverShell.App.Input;
using OverShell.App.Terminal.WindowsTerminal;
using OverShell.Core;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    /// <summary>Actual native drop callbacks and ConPTY input; no system clipboard or synthetic mouse input.</summary>
    private static async Task RunTransfersAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (transfers) start ===");
        var pass = true;
        void Check(bool ok, string description)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {description}");
        }

        var root = Path.Combine(AppPaths.StateRoot, "transfer-probe");
        Directory.CreateDirectory(root);
        var tab = window.AddTab(first.Profile with
        {
            Id = "transfer-probe", Name = "Transfer probe", CommandLine = "powershell.exe -NoLogo -NoProfile", StartingDirectory = root,
        }, activate: true);
        await TransferUntilAsync(() => tab.IsRunning && tab.TerminalHwnd != 0);
        await Task.Delay(1500);
        var surface = (WindowsTerminalSurface)tab.Surface;
        var target = surface.DropTarget;
        Check(target is { IsRegistered: true } && target.Hwnd == tab.TerminalHwnd, "OLE drop target is registered on the real terminal HWND");
        if (target is null) return;

        var errors = new List<string>();
        tab.InputFailed += (_, error) => errors.Add(error);
        string[] paths = [Path.Combine(root, "a b's $literal.pdf"), Path.Combine(root, "日本語 video.mp4"), Path.Combine(root, "unknown.payload"), root];
        foreach (var path in paths.Take(3)) File.WriteAllBytes(path, [0, 255, 13, 10, 128]);
        var fileData = new DataObject(DataFormats.FileDrop, paths);
        var result = await ReceiveAsync(fileData, paths.Length);
        Check(result.SequenceEqual(paths), "multi-file/folder drop reaches the real shell as literal paths, including arbitrary media types");
        Check(paths.Take(3).All(path => File.ReadAllBytes(path).SequenceEqual(new byte[] { 0, 255, 13, 10, 128 })), "dropping references preserves every original file byte");

        var hwnd = tab.TerminalHwnd;
        var tearOff = window.Detach(tab);
        await Task.Delay(300);
        Check(tearOff is not null && tab.Detached && surface.DropTarget == target && target.IsRegistered && tab.TerminalHwnd == hwnd, "drop registration survives moving the native terminal into a tear-off");
        result = await ReceiveAsync(fileData, paths.Length);
        Check(result.SequenceEqual(paths), "a tear-off drop reaches that tab while the main window shows another tab");
        window.Attach(tab);
        await Task.Delay(300);
        Check(!tab.Detached && surface.DropTarget == target && target.IsRegistered, "drop registration survives reattachment");

        var image = new DataObject();
        image.SetImage(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4));
        result = await ReceiveAsync(image, 1);
        Check(result.Length == 1 && File.Exists(result[0]) && File.ReadAllBytes(result[0]).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "bitmap drop materializes a PNG path a plain shell can use");

        var media = new DataObject("audio/wav", new MemoryStream(new byte[] { 1, 2, 0, 255 }), autoConvert: false);
        result = await ReceiveAsync(media, 1);
        Check(result.Length == 1 && File.Exists(result[0]) && File.ReadAllBytes(result[0]).SequenceEqual(new byte[] { 1, 2, 0, 255 }), "clipboard-only audio drop preserves its bytes and delivers its saved path");

        var marker = Path.Combine(root, "text-drop.txt");
        var text = new DataObject(DataFormats.UnicodeText, $"[IO.File]::WriteAllText('{Ps(marker)}', 'text drop')");
        uint effect = 1;
        target.Drop(text, 0, default, ref effect);
        tab.SendText("\r");
        Check(await TransferUntilAsync(() => File.Exists(marker) && File.ReadAllText(marker) == "text drop"), "plain text drop uses the terminal text-paste path");
        Check(errors.Count == 0, "all drop callbacks completed without input errors: " + string.Join("; ", errors));

        window.CloseTab(tab);
        Check(!target.IsRegistered, "closing the tab revokes its OLE registration");
        Log($"=== selftest (transfers) result: {(pass ? "ALL PASS" : "FAILED")} ===");

        async Task<string[]> ReceiveAsync(DataObject data, int count)
        {
            var output = Path.Combine(root, Guid.NewGuid().ToString("N") + ".txt");
            tab.SendText($"& {{ [IO.File]::WriteAllLines('{Ps(output)}', [string[]]$args, [Text.UTF8Encoding]::new($false)) }} ");
            uint accepted = 1;
            target.Drop(data, 0, default, ref accepted);
            tab.SendText("\r");
            Check(accepted == 1, "native drop callback reports COPY");
            await TransferUntilAsync(() => File.Exists(output) && File.ReadAllLines(output).Length == count);
            return File.Exists(output) ? File.ReadAllLines(output) : [];
        }

        static string Ps(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static async Task<bool> TransferUntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            try { if (condition()) return true; }
            catch (IOException) { }
            await Task.Delay(50);
        }

        return false;
    }
}
