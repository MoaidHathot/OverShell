using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OverShell.App.Extensions;
using OverShell.Core;
using OverShell.Core.Settings;
using OverShell.Core.Tabs;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static bool TabNavigationMode => Mode is "hints" or "details" or "details1" or "details2";

    private static async Task RunHintsAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (hints) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var extension = window.Extensions.Loaded.OfType<TabHintsExtension>().SingleOrDefault();
        Check(extension is not null, "tab hints are a loaded first-party extension");
        if (extension is null) return;
        var oldSettings = File.ReadAllText(AppPaths.SettingsFile);
        var oldKeys = File.Exists(AppPaths.KeybindingsFile) ? File.ReadAllText(AppPaths.KeybindingsFile) : null;
        var created = new List<TerminalTab>();
        TerminalTab Add()
        {
            var tab = window.AddTab(first.Profile with { CommandLine = "cmd.exe /d /q", Name = "Hint fixture" }, activate: false, userLabel: "Same name");
            created.Add(tab);
            return tab;
        }
        bool Press(Key key, ModifierKeys modifiers = ModifierKeys.None) => window.DispatchChord(key, modifiers);
        void Pick() => window.Commands.TryExecute("tab.hints.pick");
        void Type(string hint) { foreach (var character in hint) Press(Enum.Parse<Key>(char.IsAsciiDigit(character) ? "D" + character : character.ToString())); }
        bool Assigned() => window.Shell.DisplayOrder.Select((t, i) => t.Badge?.Text == TabHints.Assign(window.Shell.DisplayOrder.Select(t => t.Id).ToArray())[i].Text).All(ok => ok);
        void Settings(string json)
        {
            File.WriteAllText(AppPaths.SettingsFile, Jsonc.Serialize(Jsonc.Merge(Jsonc.Parse(oldSettings, out _), Jsonc.Parse(json, out _))));
            window.ReloadConfiguration(all: false, settings: true);
        }
        try
        {
            var second = Add();
            var third = Add();
            await TransferUntilAsync(() => second.IsRunning && third.IsRunning);
            Check(Assigned() && second.Label == third.Label && second.Badge?.Text != third.Badge?.Text,
                "duplicate names get distinct small hints without modifying either name");
            Check(window.HintFor("tab.hints.pick") == "Ctrl+Shift+Q", "the default picker chord comes from the extension key layer");
            window.Tabs.Move(window.Tabs.IndexOf(third), 0);
            window.SetGroup(first, "paired");
            window.SetGroup(third, "paired");
            await Task.Delay(150);
            Check(Assigned(), "hints follow grouped display order after movement and group edits");
            var rendered = FindAll<TextBlock>(window.Strip).Where(t => t.Name == "TabHint").Select(t => ((TerminalTab)t.DataContext).Id).ToArray();
            Check(rendered.SequenceEqual(window.Shell.DisplayOrder.Select(t => t.Id)), "the extension's display order agrees with the actual grouped WPF strip");
            var groupHit = FindAll<TextBlock>(window.Strip).Single(t => t.Name == "TabHint" && ReferenceEquals(t.DataContext, third));
            Check(window.Strip.IndexAt(groupHit.TransformToAncestor(window.Strip).Transform(new Point(groupHit.ActualWidth / 2, groupHit.ActualHeight / 2))) == window.DisplayOrder.IndexOf(third),
                "drag hit-testing finds tabs inside grouped item containers");
            SaveVisual(window.Strip, "overshell-hints-strip.png");

            window.ActiveTab = first;
            Pick();
            var captured = extension.Hints.ToArray();
            var chosen = captured.Single(h => h.TabId == second.Id);
            window.Tabs.Move(window.Tabs.IndexOf(second), 0);
            Check(extension.Picking && captured.All(h => window.Shell.Find(h.TabId)?.Badge?.Text == h.Text), "a pending pick keeps its ID mapping and visible hints during reorder");
            Type(chosen.Text);
            Check(window.ActiveTab == second && !extension.Picking && Assigned(), "typing a hint selects the originally indicated tab, then recomputes idle hints");
            Pick();
            var shifted = extension.Hints.Single(h => h.Text == "1").TabId;
            Check(Press(Key.D1, ModifierKeys.Shift) && window.ActiveTab?.Id == shifted && !extension.Picking,
                "digit hints also accept Shift for keyboard layouts with shifted numbers");
            Pick();
            var inputHwnd = second.TerminalHwnd;
            Check(window.RouteHintKeyForDiagnostics(inputHwnd, Key.D1) && !extension.Picking &&
                  window.RouteHintKeyForDiagnostics(inputHwnd, Key.D1, repeat: true) &&
                  window.RouteHintKeyForDiagnostics(inputHwnd, Key.D1, released: true) &&
                  !window.RouteHintKeyForDiagnostics(inputHwnd, Key.D1),
                "the native router consumes selection-key repeats and key-up, then releases a fresh press");
            Pick();
            Check(Press(Key.Escape) && !extension.Picking && window.CurrentKeyMode is null, "Escape closes the mode and releases its key capture");
            Pick();
            Check(Press(Key.OemMinus) && !extension.Picking && !Press(Key.OemMinus), "an invalid hint is consumed once and ordinary input resumes afterwards");
            Pick();
            window.CurrentKeyMode!.Expire(DateTimeOffset.Now.AddMinutes(2));
            Check(!extension.Picking && window.Tabs.All(t => t.Badge?.Emphasized == false), "timeout removes selection emphasis even before the first hint key");

            Settings("""{"extensions":{"tabs.hints":{"display":"onDemand"}}}""");
            Check(window.Tabs.All(t => t.Badge is null), "on-demand display hides idle hints live");
            Pick();
            Check(window.Tabs.All(t => t.Badge?.Emphasized == true), "on-demand hints appear and emphasize only during selection");
            Press(Key.Escape);
            Check(window.Tabs.All(t => t.Badge is null), "cancellation restores on-demand visibility");
            window.Commands.TryExecute("tab.hints.toggle");
            Check(Assigned(), "the toggle command makes hints visible without editing tab names");

            File.WriteAllText(AppPaths.KeybindingsFile, """[{"keys":"ctrl+shift+q","command":"unbound"},{"keys":"ctrl+shift+f10","command":"tab.hints.pick"}]""");
            window.ReloadConfiguration(all: false, keybindings: true);
            Check(!Press(Key.Q, ModifierKeys.Control | ModifierKeys.Shift) && Press(Key.F10, ModifierKeys.Control | ModifierKeys.Shift), "user unbinding and replacement shortcuts override extension defaults");
            Check(extension.Picking && second.Badge!.Help!.Contains("F10", StringComparison.Ordinal), "badge help follows the effective user shortcut");
            Press(Key.Escape);

            Settings("""{"extensions":{"tabs.hints":{"directModifiers":"alt"}}}""");
            var direct = extension.Hints.Single(h => h.TabId == first.Id);
            Check(Press(Enum.Parse<Key>("D" + direct.Text), ModifierKeys.Alt) && window.ActiveTab == first, "optional generated direct shortcuts navigate by the current hint");
            Check(window.HintFor("view.terminal") == "Ctrl+Shift+1" && window.HintFor("view.dashboard") == "Ctrl+Shift+3", "view shortcuts keep their existing meanings");
            Settings("""{"extensions":{"tabs.hints":{"directModifiers":"ctrl+shift"}}}""");
            Check(window.HintFor("view.terminal") == "Ctrl+Shift+1" && window.HintFor("tab.close") == "Ctrl+Shift+W" && window.Keybindings.Problems.Count == 0,
                "even a conflicting modifier choice skips occupied keys and leader prefixes");
            Settings("""{"extensions":{"tabs.hints":{"directModifiers":"alt"}}}""");
            File.WriteAllText(AppPaths.KeybindingsFile, """[{"keys":"alt+2","command":"tab.hints.toggle"}]""");
            window.ReloadConfiguration(all: false, keybindings: true);
            var before = window.ActiveTab;
            Press(Key.D2, ModifierKeys.Alt);
            Check(window.ActiveTab == before && window.Tabs.All(t => t.Badge is null), "an explicit user binding wins over a generated direct binding");
            Settings("""{"extensions":{"tabs.hints":{"display":"always"}}}""");

            window.ActiveTab = first;
            Pick();
            var closedHint = extension.Hints.Single(h => h.TabId == third.Id).Text;
            window.CloseTab(third);
            Add();
            Type(closedHint);
            Check(window.ActiveTab == first && !extension.Picking, "closing a target and adding a replacement cannot redirect a pending hint");

            window.Commands.TryExecute("view.herd");
            window.Commands.TryExecute("sidebar.focus");
            Pick();
            Check(window.Shell.Cursor is null && extension.Picking, "the scoped hint mode relinquishes the sidebar's previous keyboard cursor");
            Type(extension.Hints.Single(h => h.TabId == second.Id).Text);
            Check(window.ActiveTab == second, "hint selection works from the herd sidebar");
            window.Commands.TryExecute("view.dashboard");
            await Task.Delay(150);
            Check(FindAll<TextBlock>(window.DashboardView).Any(t => t.Name == "TabHint" && t.Text.Length > 0), "dashboard cards show the same tab hints");
            Pick();
            Type(extension.Hints.Single(h => h.TabId == first.Id).Text);
            Check(window.ActiveTab == first && !window.DashboardVisible, "choosing a dashboard hint opens the interactive terminal");

            var pid = second.Session.ProcessId;
            var hwnd = second.TerminalHwnd;
            window.Presentations.ShowFloating(second.Id, activate: false);
            Pick();
            Type(extension.Hints.Single(h => h.TabId == second.Id).Text);
            Check(window.ActiveTab == second && !second.Floating && second.Session.ProcessId == pid && second.TerminalHwnd == hwnd, "hint activation uses the presentation API and preserves a floating terminal's process and surface");
            var tear = window.Detach(second)!;
            await Task.Delay(150);
            window.ExecuteForTab("tab.hints.pick", second);
            Check(extension.Picking && tear.Bars.Children.Count > 2, "the hint bar is hosted in the invoking detached window");
            window.CancelKeyMode();
            window.Attach(second);

            for (var i = window.Tabs.Count; i < 35; i++) Add();
            await Task.Delay(350);
            Check(extension.Hints.Count == 35 && extension.Hints[^1].Text.Length > 1, "crowded strips receive unambiguous multi-key overflow hints");
            var overflow = extension.Hints[33];
            Pick();
            Press(Key.NumPad0);
            Check(extension.Picking && window.CurrentKeyMode?.Pending.Count == 1, "the numeric keypad can start an overflow hint");
            Press(Key.NumPad1);
            Check(window.ActiveTab?.Id == overflow.TabId && !extension.Picking, "a multi-key hint reaches an off-screen tab without confusing it with tab 1");
            foreach (var layout in new[] { "top", "bottom", "left-list", "right-list", "left-rail" })
            {
                window.Commands.TryExecute("layout." + layout);
                await Task.Delay(120);
                var hints = FindAll<TextBlock>(window.Strip).Where(t => t.Name == "TabHint").ToArray();
                Check(hints.Length == window.Tabs.Count && hints.All(t => t.Text == ((TerminalTab)t.DataContext).Badge?.Text && t.ActualHeight > 0), $"{layout}: every real template renders its tab's hint");
                var activeHint = hints.Single(t => ReferenceEquals(t.DataContext, window.ActiveTab));
                var activeBounds = activeHint.TransformToAncestor(window.Strip).TransformBounds(new Rect(activeHint.RenderSize));
                Check(new Rect(window.Strip.RenderSize).Contains(activeBounds), $"{layout}: navigation scrolls the active hint fully into view");
                SaveVisual(window.Strip, $"overshell-hints-{layout}.png");
            }
            window.Commands.TryExecute("layout.top");
            Settings("""{"theme":"light","tabs":{"twoLine":false}}""");
            await Task.Delay(150);
            var lightHint = FindAll<TextBlock>(window.Strip).Single(t => t.Name == "TabHint" && ReferenceEquals(t.DataContext, window.ActiveTab));
            Check(lightHint.IsVisible && lightHint.Foreground == window.ActiveTab!.Foreground && !window.ActiveTab.ShowsDetail,
                "single-line tabs retain readable hints in light chrome with an active dark terminal scheme");
            SaveVisual(window.Strip, "overshell-hints-light-single-line.png");
            Settings("""{"theme":"dark"}""");
            window.Commands.TryExecute("view.zen");
            Pick();
            Check(extension.Picking && window.Bars.Children.OfType<Border>().Any(), "the pick bar makes hints discoverable when the tab strip is hidden");
            Press(Key.Escape);
            window.Commands.TryExecute("view.terminal");
            window.Commands.TryExecute("layout.top");

            Pick();
            window.Commands.TryExecute("palette.commands");
            await Task.Delay(150);
            Check(!extension.Picking && window.CurrentKeyMode is null, "opening an owned picker cancels hint capture on focus loss");
            window.Palette?.Close();
            Settings("""{"extensions":{"tabs.hints":{"enabled":false}}}""");
            Check(window.Tabs.All(t => t.Badge is null) && !window.Commands.TryExecute("tab.hints.pick") && window.HintFor("tab.hints.pick") is null, "disabling the extension live clears badges, mode and generated defaults");
            Settings("""{"extensions":{"tabs.hints":{"enabled":true}}}""");
            Check(Assigned() && window.Commands.TryExecute("tab.hints.pick"), "reenabling the loaded extension restores its current hints and commands");
            extension.Dispose();
            Check(window.CurrentKeyMode is null && window.Tabs.All(t => t.Badge is null) && window.Commands.Find("tab.hints.pick") is null,
                "extension disposal removes every owned badge, binding, command and pending mode");
        }
        finally
        {
            window.CancelKeyMode();
            foreach (var tab in created) if (window.Tabs.Contains(tab)) window.CloseTab(tab);
            File.WriteAllText(AppPaths.SettingsFile, oldSettings);
            if (oldKeys is null) File.Delete(AppPaths.KeybindingsFile); else File.WriteAllText(AppPaths.KeybindingsFile, oldKeys);
        }
        Log($"=== selftest (hints) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }
}
