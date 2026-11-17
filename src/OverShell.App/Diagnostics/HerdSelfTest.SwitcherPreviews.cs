using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OverShell.App.Chrome;
using OverShell.App.Extensions;
using OverShell.App.Terminal.WindowsTerminal;
using OverShell.Core;
using OverShell.Core.Previews;
using OverShell.Core.Settings;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunSwitcherPreviewsAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (switcherpreviews) start ===");
        var pass = true;
        void Check(bool ok, string description) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {description}"); }
        var tabs = new List<TerminalTab> { first };
        var mru = window.Extensions.Loaded.OfType<MruSwitcherExtension>().Single();
        var expected = Terminal.TerminalFactory.Composition.Enabled ? "live" : "styled";
        bool Released() => NativeTerminalPreview.ActiveLeases == 0 && StyledPreviewSource.ActiveSources == 0 &&
            StyledPreviewSource.PendingDrawings == 0 && tabs.All(t => t.TextPreviewReaders == 0);
        bool Ready(TerminalPreviewControl preview, string backend) => preview.HasDemand && preview.Backend == backend &&
            (backend != "styled" || preview.HasDrawing);
        try
        {
            window.Width = 1180;
            window.Height = 760;
            while (tabs.Count < 3) tabs.Add(window.AddTab(first.Profile, activate: false));
            Check(await TransferUntilAsync(() => tabs.All(t => t.IsRunning && t.TerminalHwnd != 0)), "switcher fixture terminals are running");
            await Task.Delay(600);
            for (var i = 0; i < tabs.Count; i++) tabs[i].UserLabel = $"Mirror fixture {i + 1}";
            var grids = tabs.ToDictionary(t => t, t => t.Grid);
            window.ActiveTab = first;
            window.Activate();
            first.Surface.Focus();

            window.Commands.TryExecute("palette.tabs");
            var palette = window.Palette!;
            var preview = FindAll<TerminalPreviewControl>(palette).SingleOrDefault();
            Check(preview is not null, "the tab picker contains a full-viewport terminal preview rather than a text extract");
            if (preview is null) throw new InvalidOperationException("Missing terminal preview in tab picker");
            Check(await TransferUntilAsync(() => Ready(preview, expected)), $"the tab picker uses the {expected} backend");
            Check(ProfileInput(palette).IsKeyboardFocused && ReferenceEquals(window.ActiveTab, first), "previewing preserves the search box's input focus and the active terminal");
            Check(tabs.All(t => !t.ScreenWatched && t.TextPreviewReaders == 0), "opening the switcher does not request plain-text snapshots of every tab");
            ProfileKey(palette, Key.Down);
            Check(await TransferUntilAsync(() => ReferenceEquals(preview.Tab, tabs[1]) && Ready(preview, expected)), "Down mirrors the selected background tab without activating it");
            Check(expected == "live" ? NativeTerminalPreview.ActiveLeases == 1 : StyledPreviewSource.ActiveSources == 1, "only the selected tab owns preview work");

            // Different colours at the top and bottom detect the old last-14-rows crop.
            // Sample near the far corners of the fitted viewport, not just its centre.
            var selected = tabs[1];
            selected.SendText(SwitcherBandsCommand(selected.Grid.Rows));
            TerminalPreviewFrame? frame = null;
            Check(await TransferUntilAsync(() => (frame = selected.Surface.CapturePreview()) is { } f &&
                f.Runs.Any(r => r.Row == 0 && r.Background == 0x0000ff) &&
                f.Runs.Any(r => r.Row == f.Rows - 2 && r.Background == 0xff0000)), "the source has red upper rows and blue lower rows across its full viewport");
            if (frame is null) throw new InvalidOperationException("Missing switcher fixture snapshot");
            if (OnInputDesktop)
            {
                palette.Topmost = true;
                Check(await TransferUntilAsync(() => SwitcherBandsVisible(palette, preview, frame)), "PIXELS: the layered tab picker mirrors both far corners of the complete terminal viewport");
                selected.SendText(PreviewFillCommand(0, 255, 0));
                Check(await TransferUntilAsync(() => SwitcherGreenVisible(palette, preview)), "PIXELS: output from a hidden terminal updates its switcher mirror");
            }
            else Log("  SKIP  native switcher pixel checks require the input desktop");

            window.ApplySetting(SettingsCatalog.Find("dashboard.previewMode")!, "\"styled\"");
            Check(await TransferUntilAsync(() => Ready(preview, "styled")), "preview settings reload in the open picker");
            Check(NativeTerminalPreview.ActiveLeases == 0 && StyledPreviewSource.ActiveSources == 1, "styled mode releases the native mirror and keeps one snapshot source");
            selected.SendText(SwitcherBandsCommand(selected.Grid.Rows));
            Check(await TransferUntilAsync(() => SwitcherBandsVisible(palette, preview, frame, offscreen: true)), "styled rendering preserves the full viewport's top and bottom colours on a private desktop too");
            if (OnInputDesktop)
                Check(await TransferUntilAsync(() => SwitcherBandsVisible(palette, preview, frame)), "PIXELS: styled fallback renders faithfully inside the layered picker");

            ProfileInput(palette).Text = "@2";
            Check(await TransferUntilAsync(() => Ready(preview, "styled")) && ReferenceEquals(preview.Tab, selected) && preview.ActualHeight >= 200,
                "filtering down to one result retains a useful preview height");
            ProfileInput(palette).Text = "no-match-for-any-terminal-98765";
            Check(await TransferUntilAsync(Released) && preview.Tab is null && ProfileRows(palette).Count == 0, "an empty search clears the old image and releases preview work");
            ProfileInput(palette).Text = ">";
            Check(await TransferUntilAsync(Released) && preview.Tab is null && !preview.IsVisible, "command mode has no hidden terminal preview or text readers");
            ProfileInput(palette).Text = "@3";
            Check(await TransferUntilAsync(() => ReferenceEquals(preview.Tab, tabs[2]) && Ready(preview, "styled")), "returning to tabs attaches the new selection");
            var closed = tabs[2];
            window.CloseTab(closed);
            Check(await TransferUntilAsync(Released) && preview.Tab is null && ProfileRows(palette).Count == 0, "closing the previewed tab removes its picker row and drawing subscription");
            tabs.Remove(closed);
            ProfileInput(palette).Text = "@2";
            await TransferUntilAsync(() => Ready(preview, "styled"));
            if (OnInputDesktop)
            {
                selected.SendText("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)\r");
                selected.SendText(PreviewFixtureCommand(1));
                await TransferUntilAsync(() => selected.Surface.CapturePreview()?.Runs.Any(r => r.Text.Contains("中文", StringComparison.Ordinal)) == true);
                await Task.Delay(window.CurrentSettings.Dashboard.EffectiveInterval + 150);
                SaveScreen(palette, "overshell-switcher-preview.png");
            }
            ProfileKey(palette, Key.Escape);
            Check(await TransferUntilAsync(() => window.Palette is null && Released()), "Escape destroys every picker preview consumer");
            Check(await TransferUntilAsync(() => ShortcutRouter.FocusedWindow() == first.TerminalHwnd), "Escape restores the original terminal's native input focus");

            window.ApplySetting(SettingsCatalog.Find("dashboard.previewMode")!, "\"text\"");
            await TransferUntilAsync(() => window.CurrentSettings.Dashboard.EffectiveMode == "text");
            window.Commands.TryExecute("palette.tabs");
            palette = window.Palette!;
            Check(await TransferUntilAsync(() => first.TextPreviewReaders == 1) && tabs[1].TextPreviewReaders == 0, "explicit legacy mode reads only the selected terminal");
            ProfileKey(palette, Key.Down);
            Check(await TransferUntilAsync(() => first.TextPreviewReaders == 0 && tabs[1].TextPreviewReaders == 1), "legacy reader demand follows selection");
            selected.SendText("Write-Host switcher-text-refresh-marker\r");
            Check(await TransferUntilAsync(() => selected.ScreenText.Contains("switcher-text-refresh-marker", StringComparison.Ordinal)), "legacy fallback keeps updating while the dashboard is hidden");
            ProfileKey(palette, Key.Escape);
            Check(await TransferUntilAsync(Released), "closing a legacy switcher releases its text reader");

            var text = "extension preview";
            var picker = PaletteWindow.Picker(window, "?", "Empty", _ =>
            [
                new PaletteItem { Title = "Text", Preview = () => text, Invoke = () => { } },
                new PaletteItem { Title = "No preview", Invoke = () => { } },
            ]);
            try
            {
                text = "updated extension preview";
                Check(await TransferUntilAsync(() => ((TextBlock)picker.FindName("TxtPreview")).Text == text), "extension-supplied text previews still refresh");
                ProfileKey(picker, Key.Down);
                Check(((TextBlock)picker.FindName("TxtPreview")).Text.Length == 0, "a row without a preview clears the previous extension's text");
            }
            finally { picker.Close(); }
            await TransferUntilAsync(() => window.IsActive);

            window.ApplySetting(SettingsCatalog.Find("dashboard.previewMode")!, "\"auto\"");
            await TransferUntilAsync(() => window.CurrentSettings.Dashboard.EffectiveMode == "auto");
            first.Surface.Focus();
            var focus = ShortcutRouter.FocusedWindow();
            window.Commands.TryExecute("tab.mruNext");
            var overlay = mru.Overlay!;
            var mruPreview = FindAll<TerminalPreviewControl>(overlay).Single();
            Check(await TransferUntilAsync(() => Ready(mruPreview, expected)), $"the MRU switcher also uses the {expected} full-viewport backend");
            Check(!overlay.IsActive && ShortcutRouter.FocusedWindow() == focus && ReferenceEquals(window.ActiveTab, first), "the MRU preview preserves terminal focus until Ctrl is released");
            if (OnInputDesktop)
            {
                mruPreview.Tab!.SendText(SwitcherBandsCommand(mruPreview.Tab.Grid.Rows));
                Check(await TransferUntilAsync(() => SwitcherBandsVisible(overlay, mruPreview, frame)), "PIXELS: the non-activating MRU overlay mirrors the complete viewport");
            }
            mru.Commit();
            Check(await TransferUntilAsync(Released) && mruPreview.Tab is null && ReferenceEquals(window.ActiveTab, selected), "MRU commit opens the selected terminal and releases the hidden overlay's preview");

            for (var i = 0; i < 10; i++)
            {
                window.Commands.TryExecute("palette.tabs");
                await Task.Delay(60);
                ProfileKey(window.Palette!, Key.Escape);
                await Task.Delay(30);
                window.Commands.TryExecute("tab.mruNext");
                await Task.Delay(60);
                mru.Cancel();
            }
            Check(await TransferUntilAsync(Released), "repeated picker and MRU open/close cycles leave no native, styled or text consumers");
            Check(tabs.All(t => t.Grid == grids[t] && t.IsRunning), "every switcher path preserves the original terminal grids and processes");

            window.Commands.TryExecute("tab.mruNext");
            await TransferUntilAsync(() => mruPreview.HasDemand);
            window.Commands.TryExecute("palette.tabs");
            Check(!mru.IsOpen && !overlay.IsVisible && mruPreview.Tab is null, "deactivating the terminal owner cancels the MRU mirror");
            ProfileKey(window.Palette!, Key.Escape);
            Check(await TransferUntilAsync(Released), "replacing the MRU overlay with a picker leaves no stale preview work");
        }
        finally
        {
            mru.Cancel();
            window.Palette?.Close();
            foreach (var tab in tabs.Skip(1).ToArray()) if (window.Tabs.Contains(tab)) window.CloseTab(tab);
            if (_previewBenchSettings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllText(AppPaths.SettingsFile, _previewBenchSettings);
            Environment.SetEnvironmentVariable("OVERSHELL_WT_SETTINGS", _previewBenchWt);
        }
        Log($"=== selftest (switcherpreviews) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    private static string SwitcherBandsCommand(int rows) =>
        $"& {{ $e=[string][char]27; [Console]::Write($e+'[48;2;255;0;0m'+$e+'[2J'+$e+'[{Math.Max(2, rows / 2)};1H'+$e+'[48;2;0;0;255m'+$e+'[0J'+$e+'[{rows};1H'+$e+'[0m') }}\r";

    private static bool SwitcherBandsVisible(Window owner, TerminalPreviewControl preview, TerminalPreviewFrame frame, bool offscreen = false)
    {
        var fit = PreviewFit.Calculate(frame.PixelWidth, frame.PixelHeight, preview.ActualWidth, preview.ActualHeight);
        if (fit.Scale <= 0) return false;
        var top = new Point(fit.Left + fit.Width * 0.1, fit.Top + fit.Height * 0.1);
        var bottom = new Point(fit.Left + fit.Width * 0.9, fit.Top + fit.Height * 0.9);
        if (offscreen)
        {
            var red = PreviewVisualPixel(preview, top);
            var blue = PreviewVisualPixel(preview, bottom);
            return red.R > 220 && red.G < 40 && red.B < 40 && blue.B > 220 && blue.G < 40 && blue.R < 40;
        }
        top = preview.PointToScreen(top);
        bottom = preview.PointToScreen(bottom);
        if (!PreviewOwnsPixel(owner, top) || !PreviewOwnsPixel(owner, bottom)) return false;
        var r = ScreenPixel((int)top.X, (int)top.Y);
        var b = ScreenPixel((int)bottom.X, (int)bottom.Y);
        return r.R > 220 && r.G < 40 && r.B < 40 && b.B > 220 && b.G < 40 && b.R < 40;
    }

    private static bool SwitcherGreenVisible(Window owner, TerminalPreviewControl preview)
    {
        var point = preview.PointToScreen(new Point(preview.ActualWidth / 2, preview.ActualHeight / 2));
        if (!PreviewOwnsPixel(owner, point)) return false;
        var color = ScreenPixel((int)point.X, (int)point.Y);
        return color.G > 220 && color.R < 40 && color.B < 40;
    }
}
