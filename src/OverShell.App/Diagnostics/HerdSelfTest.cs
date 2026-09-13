using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;

namespace OverShell.App.Diagnostics;

/// <summary>
/// End-to-end check of the P0 herd plumbing, run inside the real application with
/// <c>OVERSHELL_SELFTEST=1</c> and written to <c>%TEMP%\overshell-selftest.log</c>. It
/// injects no input (DESIGN.md §7.8): the commands it runs go through the tab's own
/// <see cref="TerminalTab.SendText"/> into the pseudoconsole, so every OSC sequence,
/// environment variable and loopback request takes the path a real harness would take —
/// including whatever ConPTY forwards or drops.
/// </summary>
internal static class HerdSelfTest
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("OVERSHELL_SELFTEST");

    private static readonly bool Enabled = Mode is "1" or "opencode" or "session1" or "session2";

    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overshell-selftest.log");

    public static void Schedule(MainWindow window, TerminalTab firstTab)
    {
        if (!Enabled)
        {
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(3000) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                switch (Mode)
                {
                    case "opencode":
                        await RunOpenCodeAsync(window, firstTab);
                        break;
                    case "session1":
                        await RunSessionSaveAsync(window, firstTab);
                        break;
                    case "session2":
                        await RunSessionRestoreAsync(window, firstTab);
                        break;
                    default:
                        await RunAsync(window, firstTab);
                        break;
                }
            }
            catch (Exception e)
            {
                Log($"SELFTEST FAILED: {e}");
            }
            finally
            {
                Log("=== selftest end ===");
            }
        };
        timer.Start();
    }

    /// <summary>
    /// First half of the restart check: two tabs, a label, a group, a fake agent session
    /// with a harmless resume command; the window then closes itself, which writes the
    /// session file the second half reads.
    /// </summary>
    private static async Task RunSessionSaveAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (session1) start ===");
        var second = window.AddTab(tab.Profile, activate: false);
        await Task.Delay(1500);

        tab.UserLabel = "session label";
        window.SetGroup(second, "restored group");

        // A report from a make-believe integration: the tab becomes an agent with a session
        // whose resume command merely prints a marker — that is what the restore must type.
        tab.ApplyReport(new IntegrationReport(tab.Id, "selftest", 1, AgentState.Working, "selftest", "working", null, "ses-restore", "Write-Host selftest-resumed", Release: false));
        window.ActiveTab = second;
        await Task.Delay(300);

        Log($"  before close: tabs={window.Tabs.Count} active={window.Tabs.IndexOf(window.ActiveTab!)} label='{tab.UserLabel}' group='{second.Group}' resume='{tab.ResumeCommand}' agent={tab.IsAgent} state={tab.State}");
        Log($"  {(tab.ResumeCommand == "Write-Host selftest-resumed" ? "PASS" : "FAIL")}  the integration's resume command is kept on the tab");
        Log("=== selftest (session1) result: closing ===");

        // Closing the window is what saves the session; the run script sees a clean exit.
        window.Close();
    }

    /// <summary>Second half: what came back, and whether the resume command was typed into the restored agent tab.</summary>
    private static async Task RunSessionRestoreAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (session2) start ===");
        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        Log($"  restored={window.RestoredSession} tabs={window.Tabs.Count} labels=[{string.Join(", ", window.Tabs.Select(t => t.UserLabel ?? "-"))}] groups=[{string.Join(", ", window.Tabs.Select(t => t.Group ?? "-"))}] active={window.Tabs.IndexOf(window.ActiveTab!)}");
        Check(window.RestoredSession, "the session file was restored instead of the default profile");
        Check(window.Tabs.Count == 2, "both tabs came back");
        Check(window.Tabs.Count > 0 && window.Tabs[0].UserLabel == "session label", "the user's label came back");
        Check(window.Tabs.Count > 1 && window.Tabs[1].Group == "restored group", "the group came back");
        Check(window.ActiveTab is not null && window.Tabs.IndexOf(window.ActiveTab) == 1, "the active tab came back");

        // The resume command is typed once the shell is quiet; the marker must show on screen.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var typed = false;
        while (DateTime.UtcNow < deadline && !typed)
        {
            await Task.Delay(500);
            window.Tabs[0].RequestScreen();
            await Task.Delay(300);
            typed = window.Tabs[0].ScreenRows.Any(r => r.Contains("selftest-resumed", StringComparison.Ordinal) && !r.Contains("Write-Host", StringComparison.Ordinal));
        }

        Log($"  screen tail: {string.Join(" ⏎ ", window.Tabs[0].ScreenRows.TakeLast(4))}");
        Check(typed, "the resume command was typed into the restored agent tab and ran");

        Log($"=== selftest (session2) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    /// <summary>
    /// Spike 4 with the real harness: <c>opencode run</c> inside the tab, the installed
    /// OverShell plugin reporting over loopback. Records when the process probe and the
    /// plugin each recognised the agent, and every state the tab went through.
    /// </summary>
    private static async Task RunOpenCodeAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (opencode) start ===");
        var started = Stopwatch.GetTimestamp();
        var transitions = new List<string>();
        tab.StateChanged += (_, t) => transitions.Add($"+{Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s {t.From}->{t.To} ({t.Reason})");

        // A second tab so the first is unviewed: Done must stick and toasts fire.
        var second = window.AddTab(tab.Profile, activate: true);
        await Task.Delay(1500);

        var status = IntegrationInstaller.Status("opencode");
        Log($"  plugin installed={status.Installed} current={status.Current} path={status.Path}");

        tab.SendText("opencode run \"Reply with exactly the word pong and nothing else.\"\r");

        string? probeAt = null, pluginAt = null, sessionId = null;
        var sawWorking = false;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
            var t = $"+{Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s";
            if (probeAt is null && tab.Harness == "opencode" && tab.Agent.Authority == AgentAuthority.Detector)
            {
                probeAt = t;
                Log($"  {t} process probe: harness=opencode ({tab.Agent.Explain})");
            }

            if (pluginAt is null && tab.Agent.AuthoritySource == "opencode")
            {
                pluginAt = t;
                Log($"  {t} plugin report: state={tab.State} ({tab.Agent.Explain})");
            }

            sessionId ??= tab.Agent.SessionId;
            sawWorking |= tab.State == AgentState.Working;

            if (pluginAt is not null && sawWorking && tab.State is AgentState.Done or AgentState.Idle)
            {
                break;
            }
        }

        // Give the run a moment to print its answer and the shell to come back, then long
        // enough for two process probes to notice opencode.exe is gone and release the plugin.
        await Task.Delay(4000);
        var doneBeforeRelease = tab.State == AgentState.Done;
        var releaseDeadline = DateTime.UtcNow.AddSeconds(12);
        string? releasedAt = null;
        while (DateTime.UtcNow < releaseDeadline)
        {
            await Task.Delay(500);
            if (tab.Agent.Authority == AgentAuthority.Detector)
            {
                releasedAt = $"+{Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s";
                break;
            }
        }

        Log($"  transitions: {string.Join(" | ", transitions)}");
        Log($"  final: harness={tab.Harness ?? "-"} state={tab.State} unread={tab.Unread} authority={tab.Agent.Authority}/{tab.Agent.AuthoritySource ?? "-"} session={sessionId ?? "-"} summary='{tab.Summary}' explain='{tab.Agent.Explain}'");
        Log($"  probe detected at {probeAt ?? "never"}; plugin first reported at {pluginAt ?? "never"}; released at {releasedAt ?? "never"}");
        Log($"  toasts visible: {window.Notifications?.VisibleToasts ?? -1}");

        var rows = await new Agents.ScreenReader().ReadRowsAsync(MainWindow.FindTerminalHwnd(tab.View));
        Log($"  screen tail: {string.Join(" ⏎ ", (rows ?? []).TakeLast(6))}");

        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        Check(pluginAt is not null, "the OpenCode plugin reported to the endpoint");
        Check(sawWorking, "the tab was Working during the run");
        Check(doneBeforeRelease, "the run ended as Done (unviewed) and a second idle did not clear it");
        Check(sessionId is not null, $"session id recorded for resume: {sessionId}");
        Check(releasedAt is not null && tab.Harness is null, "the plugin was released once opencode.exe was gone; the tab is a shell again");
        Check(tab.State == AgentState.Done && tab.Unread, "the unseen Done survived the hand-over");

        window.ActiveTab = tab;
        await Task.Delay(500);
        Check(!window.IsActive || tab.State == AgentState.Unknown, "viewing the shell tab cleared Done to Unknown");

        window.CloseTab(second);
        Log($"=== selftest (opencode) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    private static async Task RunAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest start ===");
        var failures = 0;

        void Check(bool ok, string what)
        {
            if (!ok)
            {
                failures++;
            }

            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        var transitions = new List<(AgentState To, string Reason)>();
        tab.StateChanged += (_, t) => transitions.Add((t.To, t.Reason));
        var attention = new List<AgentAttention>();
        tab.AttentionRequested += (_, a) => attention.Add(a);

        // A second tab makes the first one "not viewed": Done must then stick and toasts may show.
        var second = window.AddTab(tab.Profile, activate: true);
        await Task.Delay(1500);
        Check(!ReferenceEquals(window.ActiveTab, tab), "first tab is in the background");

        // ---- 1. stream signals: title detection, OSC 9;4 progress, BEL, OSC 133, OSC 777 ----
        // One pwsh command line, so no prompt interleaves and resets the title.
        var script = string.Join("; ",
            "$e=[char]27; $a=[char]7",
            "Write-Host \"${e}]0;OC | OverShell | selftest${a}\"",          // title -> harness opencode (agent)
            "Start-Sleep -m 900",
            "Write-Host \"${e}]9;4;1;50${a}\"",                            // progress 1 -> Blocked
            "Start-Sleep -m 900",
            "Write-Host \"${e}]0;OC | OverShell | selftest${a}${e}]9;4;3;0${a}\"", // progress 3 -> Working
            "Start-Sleep -m 900",
            "Write-Host \"${e}]9;4;4;100${a}\"",                           // progress 4 -> Done (unviewed)
            "Start-Sleep -m 900",
            "Write-Host \"${e}]9;4;0;0${a}\"",                             // clear progress
            "Start-Sleep -m 900",
            "Write-Host \"${e}]777;notify;OverShell;approval needed${a}\"", // OSC 777 -> Blocked, if ConPTY forwards it
            "Start-Sleep -m 900",
            "Write-Host \"${e}]133;C${a}\"",                               // FTCS marks
            "Write-Host \"${a}\"",                                         // bare BEL
            "Start-Sleep -m 900",
            "Write-Host \"${e}]133;D;0${a}\"",
            "Write-Host selftest-signals-done");

        tab.SendText(script + "\r");
        await Task.Delay(9000);

        Log($"  transitions: {string.Join(" | ", transitions.Select(t => $"{t.To} ({t.Reason})"))}");
        Log($"  attention: {string.Join(" | ", attention.Select(a => $"{a.State}:{a.Reason}"))}");
        Log($"  now: harness={tab.Harness ?? "-"} agent={tab.IsAgent} state={tab.State} label='{tab.Label}' detail='{tab.Detail}' explain='{tab.Agent.Explain}'");

        Check(tab.Harness == "opencode", "title 'OC | …' detected the OpenCode harness");
        Check(transitions.Any(t => t.To == AgentState.Blocked && t.Reason.Contains("progress state 1")), "OSC 9;4;1 (question) -> Blocked");
        Check(transitions.Any(t => t.To == AgentState.Working && t.Reason.Contains("progress state 3")), "OSC 9;4;3 (busy) -> Working");
        Check(transitions.Any(t => t.To == AgentState.Done && t.Reason.Contains("progress state 4")), "OSC 9;4;4 (unseen) -> Done while not viewed");
        Check(attention.Any(a => a.State == AgentState.Blocked), "Blocked raised an attention event");
        Check(attention.Any(a => a.State == AgentState.Done), "Done raised an attention event");
        Log($"  OSC 777 forwarded by ConPTY: {(transitions.Any(t => t.Reason.Contains("notification")) ? "yes" : "no")}");
        Log($"  bare BEL forwarded by ConPTY: {(transitions.Any(t => t.Reason == "bell") || attention.Any(a => a.Reason == "bell") ? "yes" : "no (or state did not allow Done)")}");
        Log($"  toasts visible: {window.Notifications?.VisibleToasts ?? -1}");
        if (window.Notifications?.ToastVisual is { } toasts)
        {
            SaveVisual(toasts, "overshell-selftest-toasts.png");
        }

        if (window.FindName("TitleBarSurface") is System.Windows.FrameworkElement stripDuring)
        {
            SaveVisual(stripDuring, "overshell-selftest-tabstrip-done.png");
        }

        // ---- 2. viewing clears Done ----
        window.ActiveTab = tab;
        await Task.Delay(700);
        Log($"  after viewing: state={tab.State} unread={tab.Unread} windowActive={window.IsActive}");
        Check(!window.IsActive || tab.State != AgentState.Done, "Done clears when the tab is viewed in a focused window (or window is unfocused)");

        // ---- 3. process probe (on the fresh second tab, before any integration claims it):
        //         a descendant named like a harness turns the tab into that agent, and back. ----
        var fakeDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overshell-selftest");
        System.IO.Directory.CreateDirectory(fakeDir);
        var fake = System.IO.Path.Combine(fakeDir, "claude.exe");
        System.IO.File.Copy(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"), fake, overwrite: true);
        second.SendText($"& '{fake}' /c \"echo selftest-fake-claude & timeout /t 6 /nobreak > NUL\"\r");
        await Task.Delay(4500);
        Log($"  during fake claude: harness={second.Harness ?? "-"} agent={second.IsAgent} authority={second.Agent.Authority} state={second.State}");
        Check(second.Harness == "claude", "process probe found 'claude.exe' below the shell");
        await Task.Delay(5000);
        Log($"  after fake claude exited: harness={second.Harness ?? "-"} agent={second.IsAgent} state={second.State}");
        Check(second.Harness is null && !second.IsAgent, "tab is a shell again once the harness process is gone");

        // ---- 4. endpoint from inside the tab: the environment OverShell injected ----
        var seq = 100;
        var reportFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overshell-selftest-report.json");
        tab.SendText(
            $"Set-Content -NoNewline -Path '{reportFile}' -Value ('{{\"tab\":\"' + $env:OVERSHELL_TAB_ID + '\",\"source\":\"selftest\",\"seq\":{seq},\"state\":\"blocked\",\"message\":\"from inside the tab\"}}'); " +
            $"curl.exe -s -o NUL -w \"selftest-report %{{http_code}}`n\" -X POST \"$env:OVERSHELL_ENDPOINT/v1/report?token=$env:OVERSHELL_TOKEN\" -H \"Content-Type: application/json\" --data-binary \"@{reportFile}\"\r");
        await Task.Delay(3000);
        Log($"  after report: state={tab.State} authority={tab.Agent.Authority} explain='{tab.Agent.Explain}'");
        Check(tab.Agent.Authority == AgentAuthority.Integration && tab.State == AgentState.Blocked, "POST /v1/report from the tab's environment made the integration authoritative (Blocked)");

        // ---- 5. the Copilot hook shim, verbatim from the hook file, body on stdin ----
        var hookFile = (JsonObject)Jsonc.Parse(IntegrationInstaller.CopilotHookContent(), out _)!;
        var command = (string)((JsonArray)((JsonObject)((JsonArray)((JsonObject)hookFile["hooks"]!)["agentStop"]!)[0]!)["args"]!)[2]!;
        tab.SendText($"'{{\"sessionId\":\"selftest-session\",\"timestamp\":{seq + 1}}}' | cmd.exe /d /c \"{command}\"; Write-Host selftest-hook-done\r");
        var hookDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < hookDeadline && tab.Agent.AuthoritySource != CopilotHookTranslator.Source)
        {
            await Task.Delay(200);
        }

        Log($"  after copilot hook: state={tab.State} authority={tab.Agent.AuthoritySource} session={tab.Agent.SessionId} harness={tab.Harness} explain='{tab.Agent.Explain}'");
        Check(tab.Agent.AuthoritySource == CopilotHookTranslator.Source, "cmd.exe + curl shim reached /v1/copilot/{tab}/agentStop");
        Check(tab.Agent.SessionId == "selftest-session", "hook payload (stdin) was parsed: sessionId recorded");
        Check(tab.Harness == "copilot", "a hook report re-labels the tab as Copilot CLI");

        // No copilot.exe runs below this shell, so two probes later the hook's authority is released.
        await Task.Delay(6500);
        Log($"  after release window: authority={tab.Agent.Authority} harness={tab.Harness ?? "-"} explain='{tab.Agent.Explain}'");
        Check(tab.Agent.Authority == AgentAuthority.Detector, "an integration with no harness process below the shell is released within two probes");

        // ---- 6. GET /v1/tabs through the endpoint (diagnostics route) ----
        tab.SendText("curl.exe -s \"$env:OVERSHELL_ENDPOINT/v1/tabs?token=$env:OVERSHELL_TOKEN\" | Set-Content \"$env:TEMP\\overshell-selftest-tabs.json\"\r");
        await Task.Delay(2000);
        var tabsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overshell-selftest-tabs.json");
        var tabsJson = System.IO.File.Exists(tabsPath) ? System.IO.File.ReadAllText(tabsPath) : string.Empty;
        Log($"  /v1/tabs: {tabsJson.Trim()}");
        Check(tabsJson.Contains(tab.Id) && tabsJson.Contains(second.Id), "GET /v1/tabs lists both tabs");

        // ---- 7. commands & keybinding resolution (no key injection: the registry is invoked directly) ----
        Check(window.Commands.Find("tab.jumpToAttention") is not null && window.Commands.Find("palette.commands") is not null, "commands registered");
        var before = window.ActiveTab;
        window.Commands.TryExecute("tab.next");
        Check(!ReferenceEquals(before, window.ActiveTab), "tab.next switched tabs");
        window.Commands.TryExecute("tab.previous");

        var tabsBefore = window.Tabs.Count;
        Check(window.DispatchChord(System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.Control), "Ctrl+T resolves through keybindings.jsonc to tab.new");
        await Task.Delay(1200);
        Check(window.Tabs.Count == tabsBefore + 1, "…and opened a tab");
        Check(!window.DispatchChord(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control), "Ctrl+C with no selection is not swallowed (reaches the shell)");
        var third = window.Tabs[^1];
        window.CloseTab(third);

        // ---- 8. the palette is an owned, activated window: Win32 focus must move into it and back ----
        window.ActiveTab = tab;
        await Task.Delay(300);
        var terminalHwnd = MainWindow.FindTerminalHwnd(tab.View);
        var focusBefore = ShortcutRouter.FocusedWindow();
        if (!window.IsActive)
        {
            // Another application holds the foreground (the machine is in use): an owned window
            // cannot take focus and closes itself on Deactivated, by design. Not a failure of ours.
            Log("  SKIP  palette focus checks: OverShell is not the foreground window");
        }
        else
        {
            window.Commands.TryExecute("palette.commands");
            await Task.Delay(700);
            var palette = window.Palette;
            var focusInPalette = ShortcutRouter.FocusedWindow();
            var paletteHwnd = palette is null ? 0 : new System.Windows.Interop.WindowInteropHelper(palette).Handle;
            var paletteItems = palette?.FindName("List") is System.Windows.Controls.ListBox list ? list.Items.Count : -1;
            Log($"  palette: open={palette?.IsVisible} hwnd=0x{paletteHwnd:X} items={paletteItems} focus before=0x{focusBefore:X} in=0x{focusInPalette:X} terminal=0x{terminalHwnd:X} foreground-ours={ShortcutRouter.ForegroundIsOurs()}");
            if (palette is null && !ShortcutRouter.ForegroundIsOurs())
            {
                // It opened and closed itself: another process took the foreground meanwhile.
                Log("  SKIP  palette focus checks: the foreground moved to another process while the palette was open");
            }
            else
            {
                Check(palette is { IsVisible: true } && paletteItems > 5, "palette.commands opened with the command list");
                Check(focusInPalette != terminalHwnd && focusInPalette != 0, "Win32 focus left the terminal for the palette (owned window, not a Popup)");
                palette?.Close();
                await Task.Delay(700);
                var focusAfter = ShortcutRouter.FocusedWindow();
                Log($"  palette closed: focus after=0x{focusAfter:X} foreground-ours={ShortcutRouter.ForegroundIsOurs()}");
                Check(focusAfter == terminalHwnd || !ShortcutRouter.ForegroundIsOurs(), "focus returned to the terminal after the palette closed (or the foreground left us)");
            }
        }

        // ---- 9. labels persist against profile + directory ----
        window.Commands.TryExecute("tab.rename");
        await Task.Delay(500);
        Check(window.Palette is { IsVisible: true }, "tab.rename opened the prompt");
        window.Palette?.Close();
        await Task.Delay(300);
        tab.UserLabel = "selftest label";
        var state = OverShell.Core.Settings.PersistedState.Load(OverShell.Core.AppPaths.StateFile);
        state.SetLabel(tab.Profile.Id, tab.WorkingDirectory, "selftest label");
        var reloaded = OverShell.Core.Settings.PersistedState.Load(OverShell.Core.AppPaths.StateFile);
        Check(reloaded.LabelFor(tab.Profile.Id, tab.WorkingDirectory) == "selftest label", "label round-trips through state.json");
        Check(tab.Label == "selftest label", "the tab shows the user's label");
        state.SetLabel(tab.Profile.Id, tab.WorkingDirectory, null);
        tab.UserLabel = null;

        // ---- 10. views and layouts (P1) ----
        window.Commands.TryExecute("view.herd");
        await Task.Delay(600);
        Log($"  herd: layout={window.CurrentLayout.Name} caption={window.CaptionHeight} right-panel={window.FindName("RightPanel") is System.Windows.FrameworkElement rp && rp.Visibility == System.Windows.Visibility.Visible} groups={window.Sidebar.Items.Count} rows={window.Sidebar.Items.Sum(g => g.Tabs.Count)}");
        Check(window.CurrentLayout.Name == "herd" && window.ViewId == "herd", "view.herd applied the herd layout");
        Check(window.Sidebar.IsVisible && window.Sidebar.Items.Sum(g => g.Tabs.Count) == window.Tabs.Count, "the Herd sidebar lists every tab, grouped by project");
        Check(window.Sidebar.Items.Count >= 1 && window.Sidebar.Items[0].Tabs.Count >= 1, "groups are non-empty");
        SaveVisual(window.Sidebar, "overshell-selftest-sidebar.png");

        window.Commands.TryExecute("view.dashboard");
        await Task.Delay(1800);
        var dashboard = (System.Windows.FrameworkElement)window.FindName("Dashboard")!;
        var terminalHost = (System.Windows.FrameworkElement)window.FindName("TerminalHost")!;
        var cards = dashboard.FindName("Cards") as System.Windows.Controls.ItemsControl;
        Log($"  dashboard: visible={dashboard.IsVisible} terminal-visible={terminalHost.IsVisible} cards={cards?.Items.Count} screen-lengths=[{string.Join(", ", window.Tabs.Select(t => t.ScreenText.Length))}] watched=[{string.Join(", ", window.Tabs.Select(t => t.ScreenWatched))}]");
        Check(dashboard.IsVisible && !terminalHost.IsVisible, "view.dashboard shows the cards and hides the terminal host");
        Check(cards?.Items.Count == window.Tabs.Count, "one card per tab");
        Check(window.Tabs.All(t => t.ScreenText.Length > 0), "every card body has screen text read through UIA while the terminals are hidden");
        Check(window.ViewId == "dashboard" && window.CurrentLayout.Name == "dashboard", "dashboard view id and layout");
        SaveVisual(dashboard, "overshell-selftest-dashboard.png");

        window.Commands.TryExecute("view.zen");
        await Task.Delay(500);
        var status = (System.Windows.FrameworkElement)window.FindName("StatusBarSurface")!;
        Log($"  zen: caption={window.CaptionHeight} status-visible={status.IsVisible} strip-visible={window.Strip.IsVisible} terminal-visible={terminalHost.IsVisible}");
        Check(window.CaptionHeight < 40 && !status.IsVisible && !window.Strip.IsVisible && terminalHost.IsVisible, "view.zen hides tabs and status and slims the caption");

        window.Commands.TryExecute("view.toggle");
        await Task.Delay(400);
        Check(window.ViewId == "dashboard", "view.toggle returns to the previous view");

        window.Commands.TryExecute("view.terminal");
        await Task.Delay(700);
        Log($"  terminal: layout={window.CurrentLayout.Name} caption={window.CaptionHeight} strip-in-caption={window.Strip.Parent is System.Windows.Controls.ContentControl { Name: "CaptionTabsHost" }} focus=0x{ShortcutRouter.FocusedWindow():X} terminal=0x{MainWindow.FindTerminalHwnd(window.ActiveTab!.View):X}");
        Check(window.CurrentLayout.Name == "top" && window.CaptionHeight > 50 && window.Strip.Parent is System.Windows.Controls.ContentControl, "view.terminal restores the top layout with the strip in the caption");
        Check(!window.IsActive || ShortcutRouter.FocusedWindow() == MainWindow.FindTerminalHwnd(window.ActiveTab!.View), "focus is back in the terminal (when the window is foreground)");
        Check(window.Tabs.All(t => !t.ScreenWatched), "screen watching stops when no view needs it");

        // Every preset, applied and rendered: placements are asserted, the pictures are evidence.
        var windowRoot = (System.Windows.FrameworkElement)window.FindName("WindowRoot")!;
        var bottomBar = (System.Windows.FrameworkElement)window.FindName("BottomBar")!;
        var leftHost = (System.Windows.FrameworkElement)window.FindName("LeftPanel")!;
        var rightHost = (System.Windows.FrameworkElement)window.FindName("RightPanel")!;
        foreach (var preset in new[] { "bottom", "left-rail", "left-list", "right-list", "herd", "zen", "top" })
        {
            Check(window.ApplyLayoutByName(preset), $"layout preset '{preset}' exists");
            await Task.Delay(350);
            var layout = window.CurrentLayout;
            Log($"  layout {preset}: tabs={layout.Tabs.Placement}/{layout.Tabs.EffectiveStyle} strip-parent={window.Strip.Parent?.GetType().Name} strip-width={window.Strip.Width} bottom={bottomBar.IsVisible} left={leftHost.IsVisible} right={rightHost.IsVisible} sidebar={window.Sidebar.IsVisible} status={status.IsVisible} caption={window.CaptionHeight}");
            SaveVisual(windowRoot, $"overshell-selftest-layout-{preset}.png");
        }

        Check(window.CurrentLayout.Name == "top" && window.Strip.Parent is System.Windows.Controls.ContentControl { Name: "CaptionTabsHost" }, "the cycle ends back on the top preset");

        // ---- 11. layouts through configuration: a user layout file, hot-reloaded ----
        var configRoot = OverShell.Core.AppPaths.ConfigRoot;
        var tempConfig = configRoot.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
        if (!tempConfig)
        {
            Log($"  SKIP  hot-reload checks: OVERSHELL_CONFIG_DIR is not under %TEMP% ({configRoot}); not touching real configuration");
        }
        else
        {
            System.IO.Directory.CreateDirectory(OverShell.Core.AppPaths.LayoutsDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(OverShell.Core.AppPaths.LayoutsDir, "top.jsonc"),
                """{ "description": "selftest override", "tabs": { "placement": "left", "style": "list", "width": 210 }, "sidebar": { "placement": "right", "width": 260 } }""");
            System.IO.File.WriteAllText(OverShell.Core.AppPaths.KeybindingsFile, """[ { "keys": "ctrl+alt+9", "command": "tab.new" }, { "keys": "ctrl+t", "command": "unbound" } ]""");
            await Task.Delay(1500);

            var leftPanel = (System.Windows.FrameworkElement)window.FindName("LeftPanel")!;
            Log($"  after reload: layout={window.CurrentLayout.Name} tabs={window.CurrentLayout.Tabs.Placement}/{window.CurrentLayout.Tabs.EffectiveStyle} width={window.Strip.Width} left-visible={leftPanel.IsVisible} sidebar-visible={window.Sidebar.IsVisible} caption={window.CaptionHeight}");
            Check(window.CurrentLayout.Tabs.Placement == OverShell.Core.Layout.TabsPlacement.Left && leftPanel.IsVisible && Math.Abs(window.Strip.Width - 210) < 0.5, "a user layouts\\top.jsonc was picked up and applied live: tab list on the left");
            Check(window.Sidebar.IsVisible && window.CurrentLayout.Sidebar.Placement == OverShell.Core.Layout.SidePlacement.Right, "…with the sidebar on the right");
            Check(window.CaptionHeight < 40, "…and a slim caption, since the strip left it");

            var tabsBeforeReload = window.Tabs.Count;
            Check(window.DispatchChord(System.Windows.Input.Key.D9, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt), "a reloaded keybindings.jsonc binds Ctrl+Alt+9 to tab.new");
            await Task.Delay(1000);
            Check(window.Tabs.Count == tabsBeforeReload + 1, "…and it opened a tab");
            Check(!window.DispatchChord(System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.Control), "…and \"unbound\" removed Ctrl+T");
            window.CloseTab(window.Tabs[^1]);

            System.IO.File.Delete(System.IO.Path.Combine(OverShell.Core.AppPaths.LayoutsDir, "top.jsonc"));
            System.IO.File.Delete(OverShell.Core.AppPaths.KeybindingsFile);
            await Task.Delay(1500);
            Log($"  after removing overrides: layout tabs={window.CurrentLayout.Tabs.Placement} caption={window.CaptionHeight}");
            Check(window.CurrentLayout.Tabs.Placement == OverShell.Core.Layout.TabsPlacement.Top && window.CaptionHeight > 50, "deleting the override restores the preset live");
            Check(window.DispatchChord(System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.Control), "…and Ctrl+T is bound again");
            await Task.Delay(800);
            window.CloseTab(window.Tabs[^1]);
        }

        // ---- 12. git decoration: a tab that starts in a repository shows its branch ----
        var repo = OverShell.Core.Git.GitRepository.FindRoot(AppContext.BaseDirectory) ?? "W:\\Github\\OverShell";
        if (System.IO.Directory.Exists(System.IO.Path.Combine(repo, ".git")) || System.IO.File.Exists(System.IO.Path.Combine(repo, ".git")))
        {
            // The shell in this profile emits no OSC 9;9 / OSC 7, so `cd` would not be seen; a
            // profile starting in the repository exercises the same path from the first heartbeat.
            var repoTab = window.AddTab(tab.Profile with { StartingDirectory = repo }, activate: false);
            var gitDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < gitDeadline && repoTab.Branch is null)
            {
                await Task.Delay(250);
            }

            Log($"  git: cwd='{repoTab.WorkingDirectory}' root='{repoTab.GitRoot}' branch='{repoTab.Branch}' project='{repoTab.Project}' detail='{repoTab.SidebarDetail}' header='{repoTab.ProjectAndBranch}'");
            Check(repoTab.Branch == OverShell.Core.Git.GitRepository.ReadBranch(repo), "a tab in a repository shows the branch from .git/HEAD");
            Check(repoTab.ProjectAndBranch.EndsWith(repoTab.Branch ?? "?", StringComparison.Ordinal), "ProjectAndBranch carries it for cards and the sidebar");
            Check(repoTab.Project == new System.IO.DirectoryInfo(repo).Name, "the project is the repository's directory name");
            window.CloseTab(repoTab);
        }
        else
        {
            Log($"  SKIP  git checks: no repository found from {AppContext.BaseDirectory}");
        }

        // ---- 13. P2: prompt bar, groups, explain, protocol handoff, skin, Claude shim ----
        window.ActiveTab = second;
        await Task.Delay(300);
        window.Commands.TryExecute("prompt.toggle");
        await Task.Delay(400);
        Check(window.PromptBarView.IsVisible, "prompt.toggle shows the prompt bar");
        window.PromptBarView.Text = "Write-Host selftest-prompt-ok";
        window.PromptBarView.SelectedTarget = Chrome.PromptTarget.Active;
        window.PromptBarView.Send();
        await Task.Delay(1500);
        second.RequestScreen();
        await Task.Delay(400);
        Log($"  prompt: history={window.PromptBarView.History.Count} rows={string.Join(" | ", second.ScreenRows.TakeLast(5).Select(Escape))}");
        Check(second.ScreenRows.Any(r => r.Trim() == "selftest-prompt-ok"), "the prompt bar sent the text to the active tab and it ran");
        Check(window.PromptBarView.History.Count == 1, "the prompt is in the history");

        var sent = window.SendPrompt("Write-Host selftest-broadcast", Chrome.PromptTarget.All);
        await Task.Delay(1800);
        tab.RequestScreen();
        second.RequestScreen();
        await Task.Delay(500);
        Check(sent == window.Tabs.Count(t => t.IsRunning) && tab.ScreenRows.Any(r => r.Trim() == "selftest-broadcast") && second.ScreenRows.Any(r => r.Trim() == "selftest-broadcast"), "broadcast reached every tab");

        window.Commands.TryExecute("prompt.toggle");
        await Task.Delay(300);
        Check(!window.PromptBarView.IsVisible, "prompt.toggle hides the prompt bar again");

        window.SetGroup(second, "selftest group");
        await Task.Delay(300);
        var groups = window.Strip.Items.Items.Groups;
        Log($"  groups: second.Group='{second.Group}' view groups={groups?.Count} names=[{string.Join(", ", (groups is null ? [] : groups.OfType<System.Windows.Data.CollectionViewGroup>().Select(g => g.Name?.ToString() ?? "(none)")))}]");
        Check(second.Group == "selftest group" && groups is { Count: 2 }, "a grouped tab gets its own header group in the strip");
        var before2 = window.Tabs.IndexOf(second);
        window.Commands.TryExecute("tab.moveLeft");
        await Task.Delay(200);
        Log($"  move: {before2} -> {window.Tabs.IndexOf(second)} group now '{second.Group}'");
        Check(window.Tabs.IndexOf(second) == before2 - 1 && second.Group == tab.Group, "moving a tab onto another's place adopts that tab's group");
        window.Commands.TryExecute("tab.moveRight");
        await Task.Delay(200);
        window.SetGroup(second, null);

        window.ActiveTab = tab;
        await Task.Delay(300);
        window.Commands.TryExecute("tab.explain");
        await Task.Delay(700);
        var explain = window.Explain;
        Log($"  explain: open={explain?.IsVisible} text={(explain?.Text.Length ?? 0)} chars foreground-ours={ShortcutRouter.ForegroundIsOurs()}");
        if (explain is null && !ShortcutRouter.ForegroundIsOurs())
        {
            Log("  SKIP  explain checks: the foreground moved to another process while the panel was open");
        }
        else
        {
            Check(explain is { IsVisible: true } && explain.Text.Contains("authority", StringComparison.Ordinal) && explain.Text.Contains(tab.Agent.Explain, StringComparison.Ordinal), "tab.explain opens the evidence panel with the current explanation");
            Check(explain is not null && explain.Text.Contains("transitions", StringComparison.Ordinal), "…including the transition history");
        }

        explain?.Close();
        await Task.Delay(300);

        // A second OverShell process with an overshell:// URL must hand it to us and exit.
        window.ActiveTab = tab;
        await Task.Delay(200);
        var handoff = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, OverShell.Core.Integrations.ProtocolRequest.FocusUrl(second.Id)) { UseShellExecute = false });
        var handoffExited = handoff is not null && handoff.WaitForExit(8000);
        await Task.Delay(800);
        Log($"  handoff: second process exited={handoffExited} code={(handoffExited ? handoff!.ExitCode : -1)} active={window.ActiveTab?.Id} wanted={second.Id}");
        Check(handoffExited && handoff!.ExitCode == 0, "the second instance handed over and exited 0");
        Check(ReferenceEquals(window.ActiveTab, second), "overshell://focus/<tab> switched to that tab in the running window");

        if (tempConfig)
        {
            var accent = (System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.FindResource("Accent.Base");
            var original = accent.Color;
            System.IO.Directory.CreateDirectory(OverShell.Core.AppPaths.SkinsDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(OverShell.Core.AppPaths.SkinsDir, "selftest.xaml"),
                """<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><SolidColorBrush x:Key="Accent.Base" Color="#FF00FF00" /></ResourceDictionary>""");
            System.IO.File.WriteAllText(OverShell.Core.AppPaths.SettingsFile, """{ "skin": "selftest" }""");
            await Task.Delay(1500);
            Log($"  skin: Accent.Base {original} -> {accent.Color} (skin '{Chrome.SkinLoader.CurrentName}')");
            Check(accent.Color == System.Windows.Media.Color.FromArgb(0xFF, 0x00, 0xFF, 0x00), "a skin named in settings.jsonc recoloured a theme brush live");
            System.IO.File.WriteAllText(OverShell.Core.AppPaths.SettingsFile, """{ "skin": null }""");
            await Task.Delay(1500);
            Check(accent.Color == original, "removing the skin restores the original colour");
            System.IO.File.Delete(OverShell.Core.AppPaths.SettingsFile);
            await Task.Delay(800);
        }

        // The Claude shim, verbatim: the same cmd.exe + curl line the installer writes.
        var claudeCommand = IntegrationInstaller.ShimCommand("claude", "Notification");
        tab.SendText($"'{{\"session_id\":\"claude-selftest\",\"hook_event_name\":\"Notification\",\"notification_type\":\"permission_prompt\",\"message\":\"Claude needs your permission\"}}' | cmd.exe /d /c \"{claudeCommand}\"\r");
        var claudeDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < claudeDeadline && tab.Agent.AuthoritySource != ClaudeHookTranslator.Source)
        {
            await Task.Delay(200);
        }

        Log($"  claude hook: authority={tab.Agent.AuthoritySource} state={tab.State} harness={tab.Harness} session={tab.Agent.SessionId} explain='{tab.Agent.Explain}'");
        Check(tab.Agent.AuthoritySource == ClaudeHookTranslator.Source && tab.State == AgentState.Blocked && tab.Harness == "claude", "the Claude shim reached /v1/claude/{tab}/Notification and blocked the tab");
        Check(tab.Agent.SessionId == "claude-selftest", "Claude's session_id was recorded");

        window.CloseTab(second);
        await Task.Delay(300);
        Check(window.Tabs.Count == 1, "closed the second tab");

        // Leave the palette open for a moment so a screenshot can catch it, and record its
        // layout: the content border plus its shadow margin must equal the window height,
        // or the last row is being clipped.
        window.Commands.TryExecute("palette.tabs");
        await Task.Delay(1500);
        if (window.Palette is { } open)
        {
            var border = (System.Windows.Controls.Border)open.Content;
            var listBox = open.FindName("List") as System.Windows.Controls.ListBox;
            Log($"  palette.tabs layout: window={open.ActualWidth:F0}x{open.ActualHeight:F0} border={border.ActualHeight:F0}+{border.Margin.Top + border.Margin.Bottom:F0} list={listBox?.ActualHeight:F0} items={listBox?.Items.Count}");
            Check(Math.Abs(border.ActualHeight + border.Margin.Top + border.Margin.Bottom - open.ActualHeight) < 1, "palette window height matches its content");
            SaveVisual(border, "overshell-selftest-palette.png");
        }

        if (window.FindName("TitleBarSurface") is System.Windows.FrameworkElement strip)
        {
            SaveVisual(strip, "overshell-selftest-tabstrip.png");
        }

        await Task.Delay(1500);
        window.Palette?.Close();

        try
        {
            System.IO.Directory.Delete(fakeDir, recursive: true);
        }
        catch (System.IO.IOException)
        {
            // Still exiting; the next run overwrites it anyway.
        }

        Log($"=== selftest result: {(failures == 0 ? "ALL PASS" : $"{failures} FAILED")} ===");
    }

    /// <summary>Non-ASCII made visible, for log lines a console may mangle.</summary>
    private static string Escape(string s) => string.Concat(s.Select(c => c < 0x7F ? c.ToString() : $"\\u{(int)c:X4}"));

    /// <summary>Renders a WPF element exactly as laid out (no DWM shadow, no other windows on top) to <c>%TEMP%</c>.</summary>
    private static void SaveVisual(System.Windows.FrameworkElement element, string fileName)
    {
        try
        {
            var width = (int)Math.Ceiling(element.ActualWidth);
            var height = (int)Math.Ceiling(element.ActualHeight);
            if (width <= 0 || height <= 0)
            {
                Log($"  visual {fileName}: nothing to render ({width}x{height})");
                return;
            }

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(element);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(width * dpi.DpiScaleX), (int)(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(element);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
            using var stream = System.IO.File.Create(path);
            encoder.Save(stream);
            Log($"  visual saved: {path} ({width}x{height})");
        }
        catch (Exception e)
        {
            Log($"  visual {fileName} failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics only.
        }
    }
}
