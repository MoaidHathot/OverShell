using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunFloatingAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (floats) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var firstHwnd = first.TerminalHwnd;
        Check(window.Presentations.ShowFloating(first.Id).Success && first.Floating && window.Tabs.Count == 1, "the only tab can float while remaining in the tab collection");
        Check(await TransferUntilAsync(() => ShortcutRouter.FocusedWindow() == firstHwnd), "the floated only tab receives native input focus");
        Check(first.TerminalHwnd == firstHwnd && window.ActiveTab == first, "floating the active tab preserves its HWND and selected tab identity");
        var only = window.FloatingWindows.Single();
        Check(only.Owner == window && !only.ShowInTaskbar && only.IsActive, "the float is an owned interactive window without a taskbar entry");
        var hide = FindAll<Button>(only.CaptionSurface).Single(b => (b.ToolTip as string)?.StartsWith("Hide floating", StringComparison.Ordinal) == true);
        hide.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(await TransferUntilAsync(() => !first.Floating && ShortcutRouter.FocusedWindow() == firstHwnd), "the float's X hides it and restores native focus");
        Check(first.IsRunning && first.TerminalHwnd == firstHwnd && window.FloatingWindows.Count == 0, "Hide preserves the session and cleans up the floating host");

        var second = window.AddTab(first.Profile, activate: false);
        var third = window.AddTab(first.Profile, activate: false);
        var oldReader = window.ClipboardReader;
        var originalSettings = File.ReadAllText(AppPaths.SettingsFile);
        try
        {
            Check(await TransferUntilAsync(() => second.IsRunning && third.IsRunning), "background sessions have started");
            await Task.Delay(500);
            second.SendText("Write-Host FLOAT_HISTORY_SENTINEL; 1..120 | ForEach-Object { Write-Host ('history line ' + $_) }\r");
            await Task.Delay(1000);
            var before = await window.Search.FindAsync(second.TerminalHwnd, "FLOAT_HISTORY_SENTINEL", true, null);
            Check(before is { AboveViewport: > 0 }, "the fixture places a known marker in scrollback before floating");
            var hwnd = second.TerminalHwnd;
            var pid = second.Session.ProcessId;
            window.ActiveTab = first;
            window.Commands.TryExecute("prompt.toggle");
            window.PromptBarView.Text = "origin draft stays here";
            await Task.Delay(100);
            Check(window.Presentations.ShowFloating(second.Id).Success, "a background tab opens floating");
            var floating = window.FloatingWindows.Single();
            Check(await TransferUntilAsync(() => ShortcutRouter.FocusedWindow() == hwnd), "floating transfers Win32 input focus to the borrowed terminal");
            Check(window.ActiveTab == first && window.Shell.FocusedTab == second && window.Shell.TargetTab == second, "selected main tab and focused/command tab are independent");
            Check(second.Session.ProcessId == pid && second.TerminalHwnd == hwnd, "the float reuses the exact process and terminal HWND");
            var after = await window.Search.FindAsync(hwnd, "FLOAT_HISTORY_SENTINEL", true, null);
            Check(after?.Matches.Count == before?.Matches.Count, "scrollback survives native rehosting");
            Check(!window.DispatchChord(Key.Escape, ModifierKeys.None) && second.Floating, "unbound Escape remains terminal input");
            window.Commands.TryExecute("terminal.find");
            Check(floating.Find.IsOpen && floating.Find.Session?.Tab == second, "Find targets the floating terminal's buffer");
            floating.Find.Close();

            var output = Path.Combine(AppPaths.StateRoot, "floating-paste.txt");
            window.ClipboardReader = _ => new Input.TransferContent($"[IO.File]::WriteAllText('{output.Replace("'", "''")}', $env:OVERSHELL_TAB_ID)", []);
            Check(window.DispatchChord(Key.V, ModifierKeys.Control | ModifierKeys.Shift), "terminal paste is handled in the floating window");
            await window.ClipboardIdle;
            second.SendText("\r");
            Check(await TransferUntilAsync(() => File.Exists(output) && File.ReadAllText(output) == second.Id), "pasted input executes in the floating session, not the selected main tab");
            window.Commands.TryExecute("prompt.toggle");
            Check(await TransferUntilAsync(() => floating.PromptBar.IsKeyboardFocusWithin), "the floating window has its own focused prompt bar");
            Check(window.PromptBarView.Text == "origin draft stays here", "the underlying work's prompt draft is preserved");
            floating.PromptBar.Text = "Write-Host floating-prompt";
            await floating.PromptBar.SendAsync();
            window.Commands.TryExecute("prompt.toggle");
            Check(window.Presentations.ShowFloating(third.Id).Success && window.FloatingWindows.Count == 1 && !second.Floating, "a second target replaces the owner's float");
            Check(window.Presentations.HideFloating(third.Id).Success && window.ActiveTab == first, "hiding the replacement returns to the original main tab");
            Check(await TransferUntilAsync(() => window.PromptBarView.IsKeyboardFocusWithin), "the original prompt focus survives a chain of floating targets");
            window.Commands.TryExecute("prompt.toggle");

            var home = window.Detach(second)!;
            await Task.Delay(100);
            window.Activate();
            window.ActiveTab = first;
            Check(window.Presentations.ShowFloating(second.Id).Success && second.Detached && second.Floating, "a detached tab can also be borrowed by a float");
            window.Presentations.HideFloating(second.Id);
            Check(Window.GetWindow(second.View) == home && second.Detached && !second.Floating && second.TerminalHwnd == hwnd, "Hide returns the same surface to its original tear-off");
            window.Attach(second);
            window.ActiveTab = first;
            window.ShowViewById("dashboard");
            window.Presentations.ShowFloating(second.Id);
            window.Presentations.HideFloating(second.Id);
            Check(window.ViewId == "dashboard" && window.ActiveTab == first, "a floating detour preserves dashboard view and selection");
            window.ShowViewById("terminal");
            for (var i = 0; i < 20; i++)
            {
                window.Presentations.ShowFloating(second.Id);
                window.Presentations.HideFloating(second.Id);
                await Task.Delay(20);
            }
            Check(second.TerminalHwnd == hwnd && second.Session.ProcessId == pid && window.FloatingWindows.Count == 0 && window.RoutedWindowCount == 0, "twenty show/hide cycles preserve process/HWND and release every host and input registration");
            window.Addresses.Assign(second.Id, "float-test");
            window.Commands.TryExecute("palette.tabs");
            await Task.Delay(100);
            ProfileInput(window.Palette!).Text = "@float-test";
            Check(window.DispatchChord(Key.Return, ModifierKeys.Shift), "the picker alternate action is bound through the key map");
            Check(await TransferUntilAsync(() => second.Floating), "the picker alternate action opens its selected tab floating");
            window.Commands.TryExecute("navigation.return");
            Check(!second.Floating && window.ActiveTab == first, "Return also dismisses the current floating detour");
            window.HandleArguments([ProtocolRequest.FloatUrl(second.Id)]);
            Check(await TransferUntilAsync(() => second.Floating && window.FloatingWindows.Single().IsActive), "the notification protocol opens a focused float without refocusing the main window");
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", window.Endpoint!.Token);
            var invalidDelete = await http.DeleteAsync(window.Endpoint.BaseUrl + "/v1/tabs/" + second.Id + "/presentation");
            Check((int)invalidDelete.StatusCode == 405 && window.Tabs.Contains(second), "DELETE on a presentation subresource cannot accidentally terminate the tab");
            var hideResponse = await http.PostAsync(window.Endpoint.BaseUrl + "/v1/tabs/alias:float-test/presentation", Body("{\"mode\":\"hide\"}"));
            Check(hideResponse.IsSuccessStatusCode && !second.Floating && second.IsRunning, "HTTP Hide uses the presentation service and keeps the process alive");
            var mcp = new McpServer(async (method, path, body, cancel) =>
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), window.Endpoint.BaseUrl + path);
                if (body is not null) request.Content = Body(body.ToJsonString());
                using var response = await http.SendAsync(request, cancel);
                return ((int)response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel)));
            }, "test");
            var result = await mcp.HandleAsync(JsonNode.Parse("""
                {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"overshell_present","arguments":{"tab":"alias:float-test","mode":"floating"}}}
                """), CancellationToken.None);
            Check(result?["result"]?["isError"] is null && second.Floating, "MCP presentation is a call through the real HTTP adapter");
            var retainedBar = window.FloatingWindows.Single().PromptBar;
            window.Commands.TryExecute("prompt.toggle");
            retainedBar.Text = "unsent floating draft";
            window.Presentations.HideFloating(second.Id);
            window.Presentations.ShowFloating(second.Id);
            Check(window.FloatingWindows.Single().PromptBar.Text == "unsent floating draft", "hiding and reopening preserves the float's unsent prompt draft");
            window.Presentations.HideFloating(second.Id);
            second.ApplyReport(new IntegrationReport(second.Id, "selftest", 1, AgentState.Working, "selftest", "working", null, null, null, Release: false));
            second.ApplyReport(new IntegrationReport(second.Id, "selftest", 2, AgentState.Done, "selftest", "done", null, null, null, Release: false));
            Check(second.Unread, "a background completion is unread before interaction");
            window.Presentations.ShowFloating(second.Id);
            Check(!second.Unread && second.IsFocused && !first.IsFocused, "a focused interactive float acknowledges completion, not the underlying selected tab");
            window.Presentations.HideFloating(second.Id);
            window.Notifications!.Publish(new Core.Notifications.NotificationEvent(Core.Notifications.NotificationKind.Blocked, second.Id, second.Label,
                null, null, second.Project, second.WorkingDirectory, "float notification", null, DateTimeOffset.Now, false, true));
            await Task.Delay(100);
            var toastButton = FindAll<Button>(window.Notifications.ToastHost!).FirstOrDefault(b => (b.Content as string) == "Open floating");
            Check(toastButton is not null, "in-window notifications expose Open floating");
            toastButton?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(second.Floating && window.ActiveTab == first, "the notification's floating action preserves the main selection");
            window.Presentations.HideFloating(second.Id);
            var config = JsonNode.Parse(originalSettings)!.AsObject();
            config["floating"] = new JsonObject { ["widthRatio"] = 0.55, ["heightRatio"] = 0.6, ["hideOnFocusLoss"] = true };
            config["endpoint"] = new JsonObject { ["control"] = false };
            File.WriteAllText(AppPaths.SettingsFile, config.ToJsonString());
            window.ReloadConfiguration(all: false, settings: true);
            var denied = await http.PostAsync(window.Endpoint.BaseUrl + "/v1/tabs/" + second.Id + "/presentation", Body("{\"mode\":\"floating\"}"));
            Check((int)denied.StatusCode == 403 && !second.Floating, "disabling endpoint.control live disables the new control operations");
            window.Presentations.ShowFloating(second.Id);
            Check(window.FloatingWindows.Single().Width < window.Width * 0.75, "configured floating geometry applies to the live native host");
            window.ExecuteForTab("tab.alias", second);
            await Task.Delay(150);
            var ownedPicker = Application.Current.Windows.OfType<Chrome.PaletteWindow>().Single(w => w.IsActive);
            Check(second.Floating && ownedPicker.Owner == window.FloatingWindows.Single(), "hide-on-focus-loss retains a float while its owned picker has focus");
            ProfileKey(ownedPicker, Key.Escape);
            await Task.Delay(100);
            window.Activate();
            Check(await TransferUntilAsync(() => !second.Floating), "hide-on-focus-loss dismisses the float when focus returns to the main window");
            File.WriteAllText(AppPaths.SettingsFile, originalSettings);
            window.ReloadConfiguration(all: false, settings: true);
            window.Presentations.ShowFloating(second.Id);
            window.WindowState = WindowState.Minimized;
            Check(await TransferUntilAsync(() => !second.Floating) && second.IsRunning, "minimizing the owner releases the presentation without ending the session");
            window.WindowState = WindowState.Normal;
            window.Activate();
            var closingHome = window.Detach(second)!;
            window.Activate();
            window.Presentations.ShowFloating(second.Id);
            closingHome.Close();
            Check(!second.Floating && !second.Detached && Window.GetWindow(second.View) == window && second.IsRunning, "closing a borrowed tab's home returns the surface safely to the main host");
            window.ActiveTab = first;
            window.Presentations.ShowFloating(third.Id);
            window.Commands.TryExecute("tab.close");
            Check(!window.Tabs.Contains(third) && window.Tabs.Contains(first) && window.FloatingWindows.Count == 0, "Close tab terminates the focused floating tab, not the underlying work");
        }
        finally
        {
            window.ClipboardReader = oldReader;
            File.WriteAllText(AppPaths.SettingsFile, originalSettings);
            window.ReloadConfiguration(all: false, settings: true);
            if (window.Tabs.Contains(second)) window.CloseTab(second);
            if (window.Tabs.Contains(third)) window.CloseTab(third);
            window.ShowViewById("terminal");
        }
        Log($"=== selftest (floats) result: {(pass ? "ALL PASS" : "FAILED")} ===");
        static StringContent Body(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
    }
}
