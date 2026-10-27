using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OverShell.Core;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task CheckOverflowActionsAsync(MainWindow window, Action<bool, string> check)
    {
        var strip = window.Strip;
        var more = (Button)strip.FindName("BtnOverflow");
        var profiles = (Button)strip.FindName("BtnProfiles");
        var add = (Button)strip.FindName("BtnNewTab");
        var scroller = (ScrollViewer)strip.FindName("Scroller");
        var width = window.Width;
        var height = window.Height;
        var layout = window.CurrentLayout.Name;
        var keys = File.Exists(AppPaths.KeybindingsFile) ? File.ReadAllText(AppPaths.KeybindingsFile) : null;

        // IsVisible alone also returns true for buttons clipped inside a ScrollViewer.
        // Check full bounds and hit-test their centres through the real window instead.
        bool Reachable(Button button)
        {
            if (!button.IsVisible || button.ActualWidth <= 0 || button.ActualHeight <= 0) return false;
            var origin = button.TranslatePoint(new Point(), strip);
            if (origin.X < -0.5 || origin.Y < -0.5 || origin.X + button.ActualWidth > strip.ActualWidth + 0.5 || origin.Y + button.ActualHeight > strip.ActualHeight + 0.5) return false;
            var centre = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
            var hit = window.InputHitTest(centre) as DependencyObject;
            while (hit is not null)
            {
                if (ReferenceEquals(hit, button)) return true;
                hit = VisualTreeHelper.GetParent(hit);
            }
            return false;
        }

        async Task<ContextMenu> OpenMore()
        {
            more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(100);
            return more.ContextMenu ?? throw new InvalidOperationException("The overflow button did not open its menu");
        }

        static MenuItem Action(ContextMenu menu, string id) => menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, id));
        static void Choose(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        try
        {
            check(Reachable(add) && Reachable(profiles) && Reachable(more), "crowded strip: new-tab, profile and overflow buttons are visible and hit-testable");
            var menu = await OpenMore();
            check(menu.Items.OfType<MenuItem>().Select(item => item.Tag).SequenceEqual(new[] { "palette.tabs", "tab.new", "tab.newWithProfile" }), "the actual overflow menu includes switching, default creation and profile selection");
            Choose(Action(menu, "palette.tabs"));
            check(await TransferUntilAsync(() => window.Palette is { IsVisible: true }), "the overflow switch action opens the tab switcher after closing its menu");
            check(window.Palette is not null && ProfileRows(window.Palette).Count == window.Tabs.Count, "the overflow switcher lists every tab");
            ProfileKey(window.Palette!, Key.Escape);

            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await Task.Delay(500);
            check(strip.IsOverflowing && Reachable(add) && Reachable(profiles) && Reachable(more), "at minimum window size, all three tab actions remain reachable");
            SaveVisual((FrameworkElement)window.FindName("TitleBarSurface"), "overshell-selftest-overflow-narrow.png");
            var before = profiles.TranslatePoint(new Point(), strip);
            scroller.ScrollToHorizontalOffset(scroller.ScrollableWidth);
            await Task.Delay(200);
            check(Reachable(add) && Reachable(profiles) && profiles.TranslatePoint(new Point(), strip) == before, "scrolling to the last tab leaves the creation/profile controls pinned");

            profiles.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(100);
            check(profiles.ContextMenu is { IsOpen: true } profileMenu && profileMenu.Items.OfType<MenuItem>().Count() >= window.Catalog.Profiles.Count, "the profile arrow opens the available profiles at minimum width");
            var wanted = window.Catalog.Profiles.FirstOrDefault(p => p.Name == "Overflow Cmd") ?? window.Catalog.DefaultProfile!;
            var item = profiles.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.ToolTip is string tip && tip.EndsWith("\n" + wanted.Id, StringComparison.Ordinal));
            var count = window.Tabs.Count;
            Choose(item);
            check(await TransferUntilAsync(() => window.Tabs.Count == count + 1 && window.ActiveTab?.Profile.Id == wanted.Id), "choosing a profile through the narrow-window arrow launches that exact profile");
            check(await TransferUntilAsync(() => window.ActiveTab?.TerminalHwnd != 0 && ShortcutRouter.FocusedWindow() == window.ActiveTab?.TerminalHwnd), "profile-menu selection transfers focus to the new native terminal");
            window.CloseTab(window.ActiveTab!);

            menu = await OpenMore();
            check(Action(menu, "tab.newWithProfile").InputGestureText == window.HintFor("tab.newWithProfile"), "the overflow profile action shows its configured keyboard shortcut");
            Choose(Action(menu, "tab.newWithProfile"));
            check(await TransferUntilAsync(() => window.Palette?.Title == "New tab with profile — OverShell" && ProfileInput(window.Palette).IsKeyboardFocused), "the overflow profile action opens a focused profile picker");
            ProfileInput(window.Palette!).Text = wanted.Id;
            count = window.Tabs.Count;
            ProfileKey(window.Palette!, Key.Return);
            check(await TransferUntilAsync(() => window.Tabs.Count == count + 1 && window.ActiveTab?.Profile.Id == wanted.Id), "Enter in the overflow profile picker opens the requested profile exactly once");
            window.CloseTab(window.ActiveTab!);

            menu = await OpenMore();
            count = window.Tabs.Count;
            Choose(Action(menu, "tab.new"));
            check(await TransferUntilAsync(() => window.Tabs.Count == count + 1 && window.ActiveTab?.Profile.Id == window.Catalog.DefaultProfile?.Id), "the overflow new-tab action opens the effective default");
            window.CloseTab(window.ActiveTab!);

            File.WriteAllText(AppPaths.KeybindingsFile, """[{ "keys": "ctrl+alt+n", "command": "tab.newWithProfile" }, { "keys": "ctrl+shift+n", "command": "unbound" }]""");
            check(await TransferUntilAsync(() => window.HintFor("tab.newWithProfile") == "Ctrl+Alt+N") && profiles.ToolTip is string tip && tip.Contains("Ctrl+Alt+N", StringComparison.Ordinal), "the pinned arrow's tooltip follows keybinding changes");
            menu = await OpenMore();
            check(Action(menu, "tab.newWithProfile").InputGestureText == "Ctrl+Alt+N", "the overflow menu also follows the rebound profile shortcut");
            menu.IsOpen = false;

            window.Width = width;
            foreach (var name in new[] { "left-list", "right-list", "left-rail", "bottom", "top" })
            {
                window.Commands.TryExecute("layout." + name);
                await Task.Delay(300);
                check(Reachable(add) && Reachable(profiles), $"{name}: tab creation and profile controls remain reachable with many tabs");
                if (strip.Mode != Core.Layout.TabsStyle.Strip)
                {
                    before = profiles.TranslatePoint(new Point(), strip);
                    scroller.ScrollToVerticalOffset(scroller.ScrollableHeight);
                    await Task.Delay(150);
                    check(Reachable(profiles) && profiles.TranslatePoint(new Point(), strip) == before, $"{name}: vertical scrolling leaves the profile controls pinned");
                }
            }
        }
        finally
        {
            if (more.ContextMenu is { } menu) menu.IsOpen = false;
            if (profiles.ContextMenu is { } profileMenu) profileMenu.IsOpen = false;
            window.Palette?.Close();
            window.Width = width;
            window.Height = height;
            window.Commands.TryExecute("layout." + layout);
            if (keys is null) File.Delete(AppPaths.KeybindingsFile); else File.WriteAllText(AppPaths.KeybindingsFile, keys);
        }
    }
}
