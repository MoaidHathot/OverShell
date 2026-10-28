using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using OverShell.App.Chrome;
using OverShell.App.Terminal.WindowsTerminal;
using OverShell.Core.Settings;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static void PreviewWindowKey(Window window, Key key) => window.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static void PreviewMouse(FrameworkElement element, RoutedEvent routed) => element.RaiseEvent(new MouseEventArgs(
        Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = routed });

    private static async Task CheckEnlargedPreviewsAsync(MainWindow window, IReadOnlyList<TerminalTab> tabs, Action<bool, string> check)
    {
        var grid = tabs.Select(t => t.Grid).ToArray();
        window.ActiveTab = tabs[0];
        window.Commands.TryExecute("dashboard.focus");
        window.DispatchChord(Key.Down, ModifierKeys.None);
        check(window.DispatchChord(Key.Space, ModifierKeys.None), "Space on the dashboard cursor opens an enlarged preview");
        check(await TransferUntilAsync(() => window.TerminalPreview is { KeyboardPreview: true, IsVisible: true } p && p.Preview.HasDemand), "keyboard preview has a live or styled consumer");
        var preview = window.TerminalPreview!;
        check(ReferenceEquals(preview.Tab, tabs[1]) && ReferenceEquals(window.ActiveTab, tabs[0]), "keyboard preview targets the cursor without activating its terminal");
        check(ShortcutRouter.FocusedWindow() == new WindowInteropHelper(preview).Handle, "keyboard focus belongs to the preview window, not a terminal");
        PreviewWindowKey(preview, Key.Right);
        check(ReferenceEquals(preview.Tab, tabs[2]), "arrow keys navigate enlarged previews");
        preview.Width = 680;
        preview.Height = 430;
        await Task.Delay(150);
        check(tabs.Select(t => t.Grid).SequenceEqual(grid), "resizing an enlarged preview preserves terminal dimensions");
        PreviewWindowKey(preview, Key.Escape);
        check(await TransferUntilAsync(() => window.TerminalPreview is null && window.IsActive), "Escape closes the enlarged preview and restores the owner");

        window.Commands.TryExecute("tab.preview");
        await TransferUntilAsync(() => window.TerminalPreview is { IsVisible: true });
        var chosen = window.TerminalPreview!.Tab;
        PreviewWindowKey(window.TerminalPreview, Key.Return);
        check(await TransferUntilAsync(() => window.TerminalPreview is null && window.ViewId == "terminal" && ReferenceEquals(window.ActiveTab, chosen) && ShortcutRouter.FocusedWindow() == chosen.TerminalHwnd), "Enter opens the selected terminal and restores native input focus");
        check(window.Shell.Cursor is null, "opening a previewed terminal clears the dashboard keyboard cursor");

        window.ShowViewById("dashboard");
        await Task.Delay(150);
        var card = FindAll<Border>(window.DashboardView).First(b => b.Name == "Card" && ReferenceEquals(b.DataContext, tabs[0]));
        window.DashboardView.EnsureVisible(tabs[0]);
        await Task.Delay(150);
        window.Activate();
        var focus = ShortcutRouter.FocusedWindow();
        PreviewMouse(card, Mouse.MouseEnterEvent);
        check(await TransferUntilAsync(() => window.TerminalPreview is { KeyboardPreview: false, IsVisible: true }), "the real card hover event opens a delayed enlarged preview");
        preview = window.TerminalPreview!;
        check(!preview.IsActive && ShortcutRouter.FocusedWindow() == focus, "hover preview preserves keyboard focus");
        PreviewMouse(card, Mouse.MouseLeaveEvent);
        PreviewMouse(preview, Mouse.MouseEnterEvent);
        await Task.Delay(250);
        check(ReferenceEquals(window.TerminalPreview, preview), "moving from the card into its preview keeps it open");
        PreviewMouse(preview, Mouse.MouseLeaveEvent);
        check(await TransferUntilAsync(() => window.TerminalPreview is null), "leaving both card and preview dismisses the hover window");

        window.ApplySetting(SettingsCatalog.Find("dashboard.hoverPreview")!, "false");
        await TransferUntilAsync(() => !window.CurrentSettings.Dashboard.HoverPreview);
        PreviewMouse(card, Mouse.MouseEnterEvent);
        await Task.Delay(window.CurrentSettings.Dashboard.EffectiveHoverDelay + 100);
        check(window.TerminalPreview is null, "hover enlargement can be disabled live");
        PreviewMouse(card, Mouse.MouseLeaveEvent);
        window.Commands.TryExecute("tab.preview");
        check(await TransferUntilAsync(() => window.TerminalPreview is { KeyboardPreview: true }), "keyboard enlargement remains available when hover is disabled");
        PreviewWindowKey(window.TerminalPreview!, Key.Escape);

        window.ApplySetting(SettingsCatalog.Find("dashboard.previewMode")!, "\"styled\"");
        await TransferUntilAsync(() => window.CurrentSettings.Dashboard.EffectiveMode == "styled" && FindAll<TerminalPreviewControl>(window.DashboardView).Any(p => p.HasDemand && p.HasDrawing));
        var visible = FindAll<TerminalPreviewControl>(window.DashboardView).First(p => p.HasDemand && p.HasDrawing);
        var sources = StyledPreviewSource.ActiveSources;
        window.OpenTerminalPreview(visible.Tab, keyboard: true);
        check(await TransferUntilAsync(() => window.TerminalPreview?.Preview.HasDrawing == true) && StyledPreviewSource.ActiveSources == sources, "the enlarged styled preview shares its card's snapshot and drawing");
        PreviewWindowKey(window.TerminalPreview!, Key.Escape);

        window.ShowViewById("terminal");
        window.ApplySetting(SettingsCatalog.Find("dashboard.previewMode")!, "\"text\"");
        await TransferUntilAsync(() => window.CurrentSettings.Dashboard.EffectiveMode == "text");
        window.OpenTerminalPreview(tabs[0], keyboard: true);
        check(await TransferUntilAsync(() => tabs[0].TextPreviewReaders == 1), "legacy enlarged previews acquire their own change-driven text reader");
        tabs[0].SendText("Write-Host enlarged-text-refresh-marker\r");
        check(await TransferUntilAsync(() => tabs[0].ScreenText.Contains("enlarged-text-refresh-marker", StringComparison.Ordinal)), "legacy enlarged preview text updates while the dashboard is hidden");
        PreviewWindowKey(window.TerminalPreview!, Key.Escape);
        check(tabs[0].TextPreviewReaders == 0, "closing a text preview releases its reader");

        window.ApplySetting(SettingsCatalog.Find("dashboard.previewMode")!, "\"auto\"");
        await TransferUntilAsync(() => window.CurrentSettings.Dashboard.EffectiveMode == "auto");
        var temporary = window.AddTab(tabs[0].Profile, activate: false);
        await TransferUntilAsync(() => temporary.IsRunning);
        window.OpenTerminalPreview(temporary, keyboard: true);
        await Task.Delay(100);
        window.CloseTab(temporary);
        check(window.TerminalPreview is null && NativeTerminalPreview.ActiveLeases == 0, "closing a previewed tab closes the window and releases its native lease");

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var handles = process.HandleCount;
        for (var i = 0; i < 20; i++)
        {
            window.OpenTerminalPreview(tabs[0], keyboard: true);
            await Task.Delay(50);
            PreviewWindowKey(window.TerminalPreview!, Key.Escape);
            await Task.Delay(20);
        }
        await TransferUntilAsync(() => StyledPreviewSource.PendingDrawings == 0);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(150);
        process.Refresh();
        check(NativeTerminalPreview.ActiveLeases == 0 && StyledPreviewSource.ActiveSources == 0, "repeated enlarged windows leave no native or styled consumers");
        check(process.HandleCount <= handles + 12, $"enlarged-window handle use stays bounded ({handles} → {process.HandleCount})");
        check(tabs.Select(t => t.Grid).SequenceEqual(grid), "all enlargement paths preserve source terminal grids");
        window.ApplySetting(SettingsCatalog.Find("dashboard.hoverPreview")!, "true");
        window.ShowViewById("dashboard");
    }
}
