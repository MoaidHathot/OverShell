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
    private static async Task RunPromptAttachmentsAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (promptattachments) start ===");
        var pass = true;
        void Check(bool ok, string description)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {description}");
        }

        var root = Path.Combine(AppPaths.StateRoot, "prompt-attachments");
        Directory.CreateDirectory(root);
        var profile = first.Profile with { CommandLine = "powershell.exe -NoLogo -NoProfile", StartingDirectory = root };
        var one = window.AddTab(profile, activate: true);
        var two = window.AddTab(profile, activate: false);
        one.Group = two.Group = "attachment-check";
        await TransferUntilAsync(() => one.IsRunning && two.IsRunning && one.TerminalHwnd != 0 && two.TerminalHwnd != 0);
        await Task.Delay(1500);
        string[] paths = [Path.Combine(root, "a b's $literal.pdf"), Path.Combine(root, "video.unknown"), root];
        File.WriteAllText(paths[0], "pdf fixture");
        File.WriteAllBytes(paths[1], [0, 255, 13, 10]);

        var bar = window.PromptBarView;
        var reader = bar.ClipboardReader;
        try
        {
            window.Commands.TryExecute("prompt.toggle");
            await Task.Delay(150);
            bar.FocusInput();
            bar.ClipboardReader = asFiles => Task.FromResult(TerminalClipboard.Read(new DataObject(DataFormats.FileDrop, paths), new TransferFileStore(AppPaths.ClipboardDir), asFiles, materializeImage: true));
            Check(window.DispatchChord(System.Windows.Input.Key.V, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift), "Ctrl+Shift+V is handled by the focused prompt bar");
            await bar.ClipboardIdle;
            Check(bar.Attachments.Select(a => a.Path).SequenceEqual(paths), "clipboard files become visible prompt attachments of any type");
            Check(((System.Windows.Controls.ItemsControl)bar.FindName("AttachmentItems")).Items.Count == 3 && ((System.Windows.Controls.Button)bar.FindName("BtnAttach")).IsVisible, "attachment chips and the all-files picker button are present");

            var broadcastPrefix = $"& {{ [IO.File]::WriteAllLines((Join-Path '{Ps(root)}' ($env:OVERSHELL_TAB_ID + '.txt')), [string[]]$args, [Text.UTF8Encoding]::new($false)) }}";
            bar.Text = broadcastPrefix;
            bar.SelectedTarget = Chrome.PromptTarget.Group;
            await bar.SendAsync();
            var firstResult = Path.Combine(root, one.Id + ".txt");
            var secondResult = Path.Combine(root, two.Id + ".txt");
            Check(await TransferUntilAsync(() => File.Exists(firstResult) && File.ReadAllLines(firstResult).SequenceEqual(paths)), "broadcast attachments reach the first real shell intact");
            Check(await TransferUntilAsync(() => File.Exists(secondResult) && File.ReadAllLines(secondResult).SequenceEqual(paths)), "broadcast attachments reach the second real shell intact");
            Check(!File.Exists(Path.Combine(root, first.Id + ".txt")), "the tab outside the selected group receives no broadcast");
            Check(bar.Text.Length == 0 && bar.Attachments.Count == 0 && bar.HistoryEntries.Count == 1 && bar.HistoryEntries[0].Files.SequenceEqual(paths), "acknowledged sends clear the draft and preserve files in history");

            var address = "@" + (window.Tabs.IndexOf(one) + 1);
            var only = Path.Combine(root, "attachment-only.txt");
            one.SendText($"& {{ [IO.File]::WriteAllLines('{Ps(only)}', [string[]]$args) }} ");
            bar.Text = address;
            bar.AttachFiles([paths[0]]);
            await bar.SendAsync();
            Check(await TransferUntilAsync(() => File.Exists(only) && File.ReadAllLines(only).SequenceEqual(new[] { paths[0] })), "an addressed attachment-only draft is delivered without leaking its address word");

            bar.Text = address + " @missing-attachment-target " + broadcastPrefix;
            bar.AttachFiles(paths);
            var retained = bar.CaptureDraft();
            await bar.SendAsync();
            Check(retained.SameAs(bar.CaptureDraft()) && bar.HistoryEntries.Count == 2, "a partly unmatched address rejects the whole draft and retains attachments");
            foreach (var attachment in bar.Attachments.ToArray()) bar.RemoveAttachment(attachment);
            bar.Text = address + " inspect";
            bar.AttachFiles([Path.Combine(root, "gone.pdf")]);
            retained = bar.CaptureDraft();
            await bar.SendAsync();
            Check(retained.SameAs(bar.CaptureDraft()), "a missing attachment leaves the draft editable instead of losing it");
            foreach (var attachment in bar.Attachments.ToArray()) bar.RemoveAttachment(attachment);

            var image = new DataObject();
            image.SetImage(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 255, 0, 255 }, 4));
            bar.ClipboardReader = _ => Task.FromResult(TerminalClipboard.Read(image, new TransferFileStore(AppPaths.ClipboardDir), materializeImage: true));
            var input = (System.Windows.Controls.TextBox)bar.FindName("Input");
            System.Windows.Input.ApplicationCommands.Paste.Execute(null, input);
            await bar.ClipboardIdle;
            Check(bar.Attachments.Count == 1 && File.Exists(bar.Attachments[0].Path) && bar.Attachments[0].Path.EndsWith(".png", StringComparison.Ordinal), "the text box's routed paste command attaches a bitmap as a persistent PNG");
        }
        finally
        {
            bar.ClipboardReader = reader;
            window.Commands.TryExecute("prompt.toggle");
            window.CloseTab(two);
            window.CloseTab(one);
        }

        Log($"=== selftest (promptattachments) result: {(pass ? "ALL PASS" : "FAILED")} ===");
        static string Ps(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    }

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
