using System.Windows.Input;
using System.Text.Json.Nodes;
using System.Net.Http;
using System.Net.Http.Headers;
using OverShell.Core;
using OverShell.Core.Commands;
using OverShell.Core.Input;
using OverShell.Core.Tabs;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunAliasesAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (aliases) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var keysBefore = System.IO.File.Exists(AppPaths.KeybindingsFile) ? System.IO.File.ReadAllText(AppPaths.KeybindingsFile) : null;
        var second = window.AddTab(first.Profile, activate: false);
        try
        {
            Check(await TransferUntilAsync(() => second.IsRunning), "aliased target has a live shell");
            Check(window.Addresses.Assign(second.Id, "build").Success && second.Alias == "build", "the addressing API updates the tab's runtime alias");
            Check(second.DisplayLabel.StartsWith("@build · ", StringComparison.Ordinal) && second.Tooltip.Contains("@build"), "alias is visible independently of the persistent label");
            Check(!window.Addresses.Assign(first.Id, "BUILD").Success, "case-insensitive duplicate assignment is refused");
            window.ExecuteForTab("tab.alias", second);
            await Task.Delay(100);
            var aliasPrompt = System.Windows.Application.Current.Windows.OfType<Chrome.PaletteWindow>().Single(w => w.IsActive);
            ProfileInput(aliasPrompt).Text = "edited";
            ProfileKey(aliasPrompt, Key.Return);
            Check(await TransferUntilAsync(() => second.Alias == "edited") && window.ActiveTab == first, "the alias prompt edits its captured tab without activating it");
            window.Addresses.Assign(second.Id, "build");
            window.Commands.TryExecute("palette.tabs");
            await Task.Delay(100);
            ProfileInput(window.Palette!).Text = "@build";
            Check(ProfileRows(window.Palette!).Count == 1 && ProfileRows(window.Palette!)[0].Title == second.DisplayLabel, "the tab picker resolves the exact alias");
            ProfileKey(window.Palette!, Key.Escape);
            var assigned = window.Shell.Keys.Assign(second.Id, new Keybinding("ctrl+alt+f11", "tab.activate"));
            Check(assigned.Registration is not null && second.ShortcutHint.Contains("f11"), "a tab-owned shortcut is registered and discoverable");
            Check(window.Shell.Keys.Assign(first.Id, new Keybinding("ctrl+alt+f11", "tab.activate")).Error is not null, "runtime shortcut conflicts are explained");
            window.Addresses.Assign(second.Id, "compiler");
            window.ActiveTab = first;
            Check(window.DispatchChord(Key.F11, ModifierKeys.Control | ModifierKeys.Alt) && window.ActiveTab == second, "a runtime shortcut follows the tab ID after alias rename");
            System.IO.File.WriteAllText(AppPaths.KeybindingsFile, """
                [{"keys":"ctrl+alt+f10","command":{"action":"tab.activate","target":{"alias":"compiler"}}}]
                """);
            window.ReloadConfiguration(all: false, keybindings: true);
            window.ActiveTab = first;
            Check(window.DispatchChord(Key.F10, ModifierKeys.Control | ModifierKeys.Alt) && window.ActiveTab == second, "parameterized configured shortcuts resolve aliases");
            window.ActiveTab = first;
            Check(window.DispatchChord(Key.F11, ModifierKeys.Control | ModifierKeys.Alt) && window.ActiveTab == second, "runtime bindings survive configuration reload");
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", window.Endpoint!.Token);
            var response = await http.PostAsync(window.Endpoint.BaseUrl + "/v1/tabs/" + second.Id + "/alias", new StringContent("{\"alias\":\"logs\"}", System.Text.Encoding.UTF8, "application/json"));
            Check(response.IsSuccessStatusCode && second.Alias == "logs", "HTTP alias assignment uses the same addressing service");
            var control = new ShellControl(window.Shell);
            var screen = control.ScreenAsync("alias:logs");
            await Task.Delay(80);
            window.Addresses.Assign(second.Id, "compiler");
            window.Addresses.Assign(first.Id, "logs");
            Check((await screen)?["id"]?.GetValue<string>() == second.Id, "an in-flight screen read retains its ID when the alias is reassigned");
            var waiting = control.WaitAsync("alias:compiler", [AgentState.Working], TimeSpan.FromSeconds(3), CancellationToken.None);
            await Task.Delay(80);
            window.Addresses.Assign(second.Id, "output");
            window.Addresses.Assign(first.Id, "compiler");
            second.ApplyReport(new IntegrationReport(second.Id, "selftest", 1, AgentState.Working, "selftest", "working", null, null, null, Release: false));
            Check((await waiting)["id"]?.GetValue<string>() == second.Id, "a wait retains its original target across alias reassignment");
            window.Addresses.Assign(first.Id, null);
            window.Addresses.Assign(second.Id, "compiler");
            var global = window.Shell.Keys.Assign(second.Id, new Keybinding("ctrl+alt+shift+f24", "tab.activate"), global: true);
            Check(global.Registration is not null, "a global runtime hotkey registers on the private desktop");
            window.Commands.TryExecute("session.save");
            var saved = Core.Settings.SessionSnapshot.Load(AppPaths.SessionFile, out _);
            Check(saved?.Tabs.All(t => t.Extra is null || t.Extra.Keys.All(k => !k.Contains("alias", StringComparison.OrdinalIgnoreCase))) == true,
                "runtime aliases and shortcuts are excluded from serialized extension state");
            var oldId = second.Id;
            var closingWait = control.WaitAsync("alias:compiler", [AgentState.Idle], TimeSpan.FromSeconds(3), CancellationToken.None);
            await Task.Delay(80);
            window.CloseTab(second);
            var closedResult = await closingWait;
            Check(closedResult["closed"]?.GetValue<bool>() == true && closedResult["id"]?.GetValue<string>() == oldId, "a wait reports its resolved runtime ID when the target closes");
            Check(window.Shell.Keys.Runtime.Count == 0 && window.Addresses.Resolve(new TabSelector(Alias: "compiler")).TabId is null, "closing a tab releases its alias and all local/global bindings");
            Check(window.DispatchChord(Key.F10, ModifierKeys.Control | ModifierKeys.Alt) && window.ActiveTab == first, "a missing configured alias is handled without leaking its chord to the terminal");
            window.Commands.TryExecute("tab.reopenClosed");
            var reopened = window.ActiveTab!;
            Check(reopened.Id != oldId && reopened.Alias is null && reopened.ShortcutHint.Length == 0, "reopening creates a fresh runtime identity without old quick-access assignments");
            var reuse = window.Shell.Keys.Assign(first.Id, new Keybinding("ctrl+alt+shift+f24", "tab.activate"), global: true);
            Check(reuse.Registration is not null, "closing releases the native global hotkey for reuse");
            if (reuse.Registration is { } r) window.Shell.Keys.Remove(r.Id);
            window.CloseTab(reopened);
        }
        finally
        {
            if (window.Tabs.Contains(second)) window.CloseTab(second);
            window.Addresses.Assign(first.Id, null);
            if (keysBefore is null) System.IO.File.Delete(AppPaths.KeybindingsFile); else System.IO.File.WriteAllText(AppPaths.KeybindingsFile, keysBefore);
            window.ReloadConfiguration(all: false, keybindings: true);
        }
        Log($"=== selftest (aliases) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    private static async Task RunQuickNavigationAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (quicknav) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var second = window.AddTab(first.Profile, activate: false);
        var third = window.AddTab(first.Profile, activate: false);
        Check(await TransferUntilAsync(() => second.IsRunning && third.IsRunning), "navigation targets have live sessions");
        second.ApplyReport(new IntegrationReport(second.Id, "selftest", 1, AgentState.Blocked, "selftest", "older question", null, null, null, Release: false));
        await Task.Delay(100);
        third.ApplyReport(new IntegrationReport(third.Id, "selftest", 1, AgentState.Blocked, "selftest", "newer question", null, null, null, Release: false));
        window.ActiveTab = first;
        window.ShowViewById("herd");
        Check(window.Commands.TryExecute("tab.jumpToAttention") && window.ActiveTab == second, "attention jump selects oldest waiting tab");
        Check(window.Commands.TryExecute("tab.jumpToAttention") && window.ActiveTab == third, "second attention jump selects next waiting tab");
        Check(window.Navigation.CanReturn, "the attention chain retains its origin");
        Check(window.DispatchChord(Key.H, ModifierKeys.Control | ModifierKeys.Shift) && window.ActiveTab == first, "Ctrl+Shift+H returns to the original work, skipping intermediate visits");
        Check(window.ViewId == "herd" && !window.Navigation.CanReturn, "return restores the view and consumes the origin");
        Check(await TransferUntilAsync(() => ShortcutRouter.FocusedWindow() == first.TerminalHwnd), "return restores native terminal focus");
        window.ActiveTab = second;
        Check(window.Commands.TryExecute("tab.last") && window.ActiveTab == first, "last tab remains a separate navigation command");
        Check(window.Commands.TryExecute("tab.last") && window.ActiveTab == second, "last tab bounces back");
        window.ActiveTab = first;
        window.Commands.TryExecute("tab.jumpToAttention");
        window.ActiveTab = third;
        Check(!window.Navigation.CanReturn, "intentional ordinary tab navigation ends the attention detour");
        window.ActiveTab = first;
        window.Commands.TryExecute("prompt.toggle");
        await Task.Delay(100);
        window.Commands.TryExecute("tab.jumpToAttention");
        window.Commands.TryExecute("navigation.return");
        Check(await TransferUntilAsync(() => window.PromptBarView.IsKeyboardFocusWithin), "return restores prompt input focus rather than always selecting the terminal");
        window.Commands.TryExecute("prompt.toggle");
        window.CloseTab(second);
        window.CloseTab(third);
        var remaining = window.AddTab(first.Profile, activate: false);
        window.Detach(remaining);
        window.Activate();
        window.CloseTab(first);
        Check(window.ActiveTab == remaining && !remaining.Detached && window.Tabs.Count == 1, "closing the last main-host tab brings the remaining detached terminal home");
        Check(await TransferUntilAsync(() => ShortcutRouter.FocusedWindow() == remaining.TerminalHwnd), "the surviving terminal remains keyboard accessible");
        Log($"=== selftest (quicknav) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }
}
