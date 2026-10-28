using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverShell.App.Chrome;
using OverShell.App.Terminal.WindowsTerminal;
using OverShell.Core;
using OverShell.Core.Settings;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunPreviewsAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (previews) start ===");
        var pass = true;
        void Check(bool ok, string description) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {description}"); }
        var tabs = new List<TerminalTab> { first };
        List<TerminalPreviewControl> Cards() => FindAll<TerminalPreviewControl>(window.DashboardView).ToList();
        List<TerminalPreviewControl> Visible() => Cards().Where(c => c.HasDemand).ToList();
        try
        {
            window.Width = 1180;
            window.Height = 760;
            while (tabs.Count < 8) tabs.Add(window.AddTab(first.Profile, activate: false));
            await TransferUntilAsync(() => tabs.All(t => t.IsRunning && t.TerminalHwnd != 0));
            await Task.Delay(1000);
            foreach (var tab in tabs) tab.SendText(PreviewFixtureCommand(1));
            await Task.Delay(1200);
            var grids = tabs.Select(t => t.Grid).ToArray();
            window.Commands.TryExecute("view.dashboard");
            var expected = Terminal.TerminalFactory.Composition.Enabled ? "live" : "styled";
            Check(await TransferUntilAsync(() => Visible().Count > 0 && Visible().All(c => c.Backend == expected && (expected == "live" || c.HasDrawing))), $"dashboard uses {expected} previews for visible cards");
            Check(Visible().Count < tabs.Count, "scrolled-out cards request no previews");
            Check(!tabs.Any(t => t.ScreenWatched), "faithful dashboard previews do not request duplicate plain-text UIA snapshots");
            Check(tabs.Select(t => t.Grid).SequenceEqual(grids), "showing the dashboard preserves every terminal grid");
            if (expected == "live") Check(NativeTerminalPreview.ActiveLeases == Visible().Count, "native leases are bounded by visible cards");

            var scroller = FindAll<ScrollViewer>(window.DashboardView).First();
            var originallyVisible = Visible().ToArray();
            scroller.ScrollToBottom();
            Check(await TransferUntilAsync(() => !originallyVisible[0].HasDemand && Visible().Any(c => !originallyVisible.Contains(c))), "scrolling releases old previews and activates newly visible cards");
            scroller.ScrollToTop();
            await TransferUntilAsync(() => originallyVisible.All(c => c.HasDemand));

            window.ApplySetting(SettingsCatalog.Find("dashboard.previewMode")!, "\"styled\"");
            Check(await TransferUntilAsync(() => Visible().Count > 0 && Visible().All(c => c.Backend == "styled" && c.HasDrawing)), "styled mode applies live and produces frozen drawings");
            Check(NativeTerminalPreview.ActiveLeases == 0 && StyledPreviewSource.ActiveSources == Visible().Count, "switching to styled releases native leases and shares one source per visible tab");
            var captures = NativeTerminalPreview.SnapshotCount;
            var changed = Visible()[0].Tab!;
            changed.SendText(PreviewFillCommand(0, 255, 0));
            Check(await TransferUntilAsync(() => NativeTerminalPreview.SnapshotCount > captures), "changed terminal output refreshes a visible styled preview");

            if (OnInputDesktop && await BringToFrontAsync(window))
            {
                window.Topmost = true;
                window.Activate();
                await Task.Delay(250);
                var card = Visible().First(c => ReferenceEquals(c.Tab, changed));
                var point = card.PointToScreen(new Point(card.ActualWidth / 2, card.ActualHeight / 2));
                Check(await TransferUntilAsync(() => { var c = ScreenPixel((int)point.X, (int)point.Y); return c.G > 220 && c.R < 40 && c.B < 40; }), "PIXELS: styled card paints the terminal's coloured blank cells");
                window.ApplySetting(SettingsCatalog.Find("dashboard.previewMode")!, "\"auto\"");
                await TransferUntilAsync(() => card.Backend == expected);
                changed.SendText(PreviewFillCommand(255, 0, 0));
                Check(await TransferUntilAsync(() => { var c = ScreenPixel((int)point.X, (int)point.Y); return c.R > 220 && c.G < 40 && c.B < 40; }), "PIXELS: automatic card tracks the real terminal's new colour");
                SaveScreen(window, "overshell-dashboard-previews.png");
                window.Topmost = false;
            }
            else Log("  SKIP  dashboard pixel comparison requires an unlocked desktop with the test window in front");

            window.Commands.TryExecute("view.terminal");
            Check(await TransferUntilAsync(() => NativeTerminalPreview.ActiveLeases == 0 && StyledPreviewSource.ActiveSources == 0 && StyledPreviewSource.PendingDrawings == 0), "hiding the dashboard releases preview leases, timers and pending drawing work");
            captures = NativeTerminalPreview.SnapshotCount;
            changed.SendText(PreviewFixtureCommand(5));
            await Task.Delay(1400);
            Check(NativeTerminalPreview.SnapshotCount == captures, "hidden dashboard performs no styled capture work");

            window.Commands.TryExecute("view.dashboard");
            var added = window.AddTab(first.Profile, activate: false);
            tabs.Add(added);
            Check(await TransferUntilAsync(() => added.IsRunning && added.Grid.Columns > 50 && added.Grid.Rows > 10), "a background tab created on the dashboard starts at full terminal dimensions");
            Check(added.Grid == first.Grid, "new background tab size comes from the full terminal host rather than the card");
            window.CloseTab(added);
            tabs.Remove(added);

            window.ApplySetting(SettingsCatalog.Find("dashboard.previewMode")!, "\"auto\"");
            await TransferUntilAsync(() => window.CurrentSettings.Dashboard.EffectiveMode == "auto");
            for (var i = 0; i < 10; i++)
            {
                window.Commands.TryExecute("view.dashboard");
                await Task.Delay(80);
                window.Commands.TryExecute("view.terminal");
                await Task.Delay(80);
            }
            Check(await TransferUntilAsync(() => NativeTerminalPreview.ActiveLeases == 0 && StyledPreviewSource.ActiveSources == 0 && StyledPreviewSource.PendingDrawings == 0), "repeated dashboard open/close cycles leave no preview consumers");
            Check(tabs.Select(t => t.Grid).SequenceEqual(grids), "repeated preview lifecycle changes preserve all source grids");
        }
        finally
        {
            window.Commands.TryExecute("view.terminal");
            foreach (var tab in tabs.Skip(1).ToArray()) if (window.Tabs.Contains(tab)) window.CloseTab(tab);
            if (_previewBenchSettings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllText(AppPaths.SettingsFile, _previewBenchSettings);
            Environment.SetEnvironmentVariable("OVERSHELL_WT_SETTINGS", _previewBenchWt);
        }
        Log($"=== selftest (previews) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }
}
