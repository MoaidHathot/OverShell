using System.Windows.Input;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
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
        Log($"=== selftest (quicknav) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }
}
