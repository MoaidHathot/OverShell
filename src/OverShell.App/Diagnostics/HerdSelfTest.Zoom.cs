using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Input;
using OverShell.App.Terminal.WindowsTerminal;
using OverShell.Core;
using OverShell.Core.Settings;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunFontZoomAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (zoom) start ===");
        var pass = true;
        void Check(bool ok, string description) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {description}"); }
        var originalSettings = File.ReadAllText(AppPaths.SettingsFile);
        var second = window.AddTab(first.Profile, activate: false);
        try
        {
            await WaitForPromptAsync(first);
            await WaitForPromptAsync(second);
            var baseline = first.FontSize!.Value;
            var frame = NativeTerminalPreview.Capture(first.TerminalHwnd)!;
            var cellHeight = (double)frame.PixelHeight / frame.Rows;
            var originalHwnd = first.TerminalHwnd;
            var originalPid = first.Session.ProcessId;
            Check(window.DispatchChord(Key.OemPlus, ModifierKeys.Control) && first.FontSize == baseline + 1, "Ctrl+= increases the real terminal font size by one point");
            Check(await TransferUntilAsync(() => NativeTerminalPreview.Capture(first.TerminalHwnd) is { } changed && (double)changed.PixelHeight / changed.Rows > cellHeight),
                "the native cell height changes, rather than stretching an image of the terminal");
            Check(second.FontSize == baseline, "zoom changes only its target tab");
            window.Presentations.ShowFloating(first.Id);
            Check(first.FontSize == baseline + 1 && first.TerminalHwnd == originalHwnd && first.Session.ProcessId == originalPid, "zoom survives floating with the same terminal and process");
            window.Presentations.HideFloating(first.Id);
            Check(first.FontSize == baseline + 1, "hiding a float does not reset its font size");
            window.Detach(first);
            Check(window.DispatchChord(Key.OemMinus, ModifierKeys.Control) && first.FontSize == baseline, "Ctrl+- targets the detached terminal");
            window.Commands.TryExecute("terminal.setFontSize", new JsonObject { ["target"] = new JsonObject { ["id"] = second.Id }, ["size"] = 22 });
            Check(second.FontSize == 22 && first.FontSize == baseline, "parameterized font changes target a background tab explicitly");
            window.Attach(first);
            window.Presentations.ShowFloating(second.Id);
            Check(window.DispatchChord(Key.D0, ModifierKeys.Control) && second.FontSize == second.DefaultFontSize, "Ctrl+0 resets the focused floating terminal to its profile size");
            window.Presentations.HideFloating(second.Id);
            Check(!first.SetFontSize(double.NaN).Success && !first.SetFontSize(10000).Success && first.FontSize == baseline, "invalid font sizes are rejected without changing the terminal");
            window.ApplySetting(SettingsCatalog.Find("terminal.fontSizeStep")!, "2");
            Check(await TransferUntilAsync(() => window.CurrentSettings.Terminal.FontSizeStep == 2), "font-size step reloads live");
            Check(window.DispatchChord(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift) && first.FontSize == baseline + 2, "Ctrl++ uses the configured point increment");
            window.Commands.TryExecute("terminal.zoomReset");
            Check(await TransferUntilAsync(() => NativeTerminalPreview.Capture(first.TerminalHwnd) is { } reset && Math.Abs((double)reset.PixelHeight / reset.Rows - cellHeight) < 0.1),
                "reset restores the original native font metrics");
        }
        finally
        {
            window.Presentations.HideFloating(first.Id);
            window.Presentations.HideFloating(second.Id);
            if (first.Detached) window.Attach(first);
            first.SetFontSize(null);
            window.CloseTab(second);
            File.WriteAllText(AppPaths.SettingsFile, originalSettings);
            window.ReloadConfiguration(all: false, settings: true);
        }
        Log($"=== selftest (zoom) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }
}
