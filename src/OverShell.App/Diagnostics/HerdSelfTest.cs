using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;
using OverShell.Core.Search;
using OverShell.Core.Settings;

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

    private static readonly bool Enabled = Mode is "1" or "opencode" or "opencode-resume" or "session1" or "session2" or "sessionend" or "history" or "icons" or "polish" or "cwd" or "resilience" or "ghost" or "workspaces" or "overflow";

    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overshell-selftest.log");

    public static void Schedule(MainWindow window, TerminalTab firstTab)
    {
        if (!Enabled)
        {
            return;
        }

        // The runs end by closing the window from outside with agents mid-report; the close
        // question (§12.14) is for people, and has its own mode below.
        MainWindow.AutoConfirmClose = true;

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
                    case "opencode-resume":
                        await RunOpenCodeResumeAsync(window, firstTab);
                        break;
                    case "session1":
                        await RunSessionSaveAsync(window, firstTab);
                        break;
                    case "session2":
                        await RunSessionRestoreAsync(window, firstTab);
                        break;
                    case "sessionend":
                        await RunSessionEndingAsync(window, firstTab);
                        break;
                    case "history":
                        await RunHistoryAsync(window, firstTab);
                        break;
                    case "icons":
                        await RunIconsAsync(window, firstTab);
                        break;
                    case "polish":
                        await RunPolishAsync(window, firstTab);
                        break;
                    case "cwd":
                        await RunCwdAsync(window, firstTab);
                        break;
                    case "resilience":
                        await RunResilienceAsync(window, firstTab);
                        break;
                    case "ghost":
                        await RunGhostAsync(window, firstTab);
                        break;
                    case "workspaces":
                        await RunWorkspacesAsync(window, firstTab);
                        break;
                    case "overflow":
                        await RunOverflowAsync(window, firstTab);
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
    /// with a harmless resume command, the main window moved, the second tab torn off and
    /// moved; the window then closes itself, which writes the session file the second half
    /// reads.
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

        // Placement (12.13): a distinctive main-window rectangle and a torn-off second tab in
        // a window of its own, also moved - the second half checks both came back.
        window.WindowState = System.Windows.WindowState.Normal;
        window.Left = 220;
        window.Top = 140;
        window.Width = 980;
        window.Height = 640;
        var tearOff = window.Detach(second);
        if (tearOff is not null)
        {
            tearOff.Left = 1300;
            tearOff.Top = 260;
            tearOff.Width = 720;
            tearOff.Height = 480;
        }

        await Task.Delay(500);
        Log($"  before close: tabs={window.Tabs.Count} active={window.Tabs.IndexOf(window.ActiveTab!)} label='{tab.UserLabel}' group='{second.Group}' resume='{tab.ResumeCommand}' agent={tab.IsAgent} state={tab.State} main={window.Left:F0},{window.Top:F0} {window.Width:F0}x{window.Height:F0} detached={second.Detached} tearoff={(tearOff is null ? "-" : $"{tearOff.Left:F0},{tearOff.Top:F0} {tearOff.Width:F0}x{tearOff.Height:F0}")}");
        Log($"  {(tab.ResumeCommand == "Write-Host selftest-resumed" ? "PASS" : "FAIL")}  the integration's resume command is kept on the tab");
        Log($"  {(second.Detached ? "PASS" : "FAIL")}  the second tab is detached before the close");
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
        // The tab in use was the detached one: the main window shows its neighbour (by design,
        // a detached tab is looked at in its own window) and the tear-off is what comes forward.
        Check(window.PreviousSession?.ActiveIndex == 1, "the active tab (the detached one) was recorded as active");

        // What the previous run said about itself, and where things were put.
        var previous = window.PreviousSession;
        Log($"  previous: version={previous?.Version} closeReason={previous?.CloseReason} interrupted={previous?.Interrupted} window={(previous?.Window is { } w ? $"{w.Left:F0},{w.Top:F0} {w.Width:F0}x{w.Height:F0}" : "-")} detached=[{string.Join(",", previous?.Tabs.Select(t => t.Detached) ?? [])}]");
        Check(previous is { Version: 2, CloseReason: SessionCloseReason.Closed, Interrupted: false }, "the previous run recorded a clean close");
        // The saved rectangles are whatever the windows really had before the close - a tiling
        // window manager may have moved them from where session1 put them - and the restore
        // must have asked for exactly those (the trace records the request at SourceInitialized).
        Check(previous?.Window is { Width: >= 520, Height: >= 320 }, "the main window's placement was saved");
        Check(previous?.Tabs.Count == 2 && previous.Tabs[1].Detached && previous.Tabs[1].Window is { Width: >= 400, Height: >= 240 }, "the tear-off's placement was saved on its tab");
        var trace = System.IO.File.Exists(TraceLog.Agents.Path) ? System.IO.File.ReadAllText(TraceLog.Agents.Path) : string.Empty;
        Check(previous?.Window is { } mw && trace.Contains($"main window placed at {mw.Left:F0},{mw.Top:F0} {mw.Width:F0}x{mw.Height:F0}", StringComparison.Ordinal), "the restore applied the saved main-window placement");
        Check(previous?.Tabs.ElementAtOrDefault(1)?.Window is { } tw && trace.Contains($"placed at {tw.Left:F0},{tw.Top:F0} {tw.Width:F0}x{tw.Height:F0}", StringComparison.Ordinal), "the restore applied the saved tear-off placement");

        // The tear-off is re-created after the first layout.
        await Task.Delay(800);
        var tearOff = window.TearOffs.FirstOrDefault();
        Log($"  now: main={window.Left:F0},{window.Top:F0} {window.Width:F0}x{window.Height:F0} tearoffs={window.TearOffs.Count} second.Detached={window.Tabs.ElementAtOrDefault(1)?.Detached} tearoff={(tearOff is null ? "-" : $"{tearOff.Left:F0},{tearOff.Top:F0} {tearOff.Width:F0}x{tearOff.Height:F0}")}");
        Check(window.Tabs.Count > 1 && window.Tabs[1].Detached && tearOff is not null && ReferenceEquals(tearOff.Tab, window.Tabs[1]), "the second tab came back detached, in a tear-off of its own");
        var fg = ShortcutRouter.ForegroundWindow();
        var tearOffHwnd = tearOff is null ? 0 : new System.Windows.Interop.WindowInteropHelper(tearOff).Handle;
        var mainHwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Log($"  foreground=0x{fg:X} tearoff=0x{tearOffHwnd:X} (IsActive={tearOff?.IsActive}) main=0x{mainHwnd:X} (IsActive={window.IsActive}) focus=0x{ShortcutRouter.FocusedWindow():X} ours={ShortcutRouter.ForegroundIsOurs()}");
        Check(tearOff is not null && (fg == tearOffHwnd || !ShortcutRouter.ForegroundIsOurs()), "the restored tear-off was brought forward as the window in use (or the foreground left us)");

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
        Check(window.Tabs[0].ResumeCommand == "Write-Host selftest-resumed" && window.Tabs[0].Agent.SessionId == "ses-restore", "the restored tab was seeded with the saved session id and resume command");

        var archives = SessionHistory.List(AppPaths.StateRoot);
        Log($"  archives: {archives.Count} ({string.Join("; ", archives.Select(a => $"{System.IO.Path.GetFileName(a.Path)}: {a.Tabs.Count} tab(s), {(a.Interrupted ? "interrupted" : a.CloseReason?.ToString())}"))})");
        Check(archives.Count >= 1 && archives[0].Tabs.Count == 2 && archives[0].CloseReason == SessionCloseReason.Closed, "the clean close archived the session");

        Log($"=== selftest (session2) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    /// <summary>
    /// Sign-out and restart (12.13): Windows asks with WM_QUERYENDSESSION, WPF raises
    /// SessionEnding and <em>schedules</em> Shutdown; the handler saves synchronously so the
    /// file says "sessionEnding" even if the process is ended before that Shutdown runs.
    /// The message is sent to our own window with ENDSESSION_LOGOFF in lParam, and the
    /// handler cancels the shutdown it would otherwise cause, so the window stays up for the
    /// checks; the file is read back and the next start would show the sign-out note.
    /// </summary>
    private static async Task RunSessionEndingAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (sessionend) start ===");
        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        tab.UserLabel = "signed out";
        await Task.Delay(2600); // one periodic save with the label
        var before = SessionSnapshot.Load(AppPaths.SessionFile, out _);
        Check(before is { CloseReason: null } && before.Tabs.Count == 1 && before.Tabs[0].Label == "signed out", "while running, the file has the label and no close reason");

        var cancelled = false;
        System.Windows.Application.Current.SessionEnding += (_, e) => { e.Cancel = true; cancelled = true; };

        // WPF listens for WM_QUERYENDSESSION on the Application's hidden "parking" window
        // (Application.EnsureHwndSource in dotnet/wpf), not on MainWindow; the system
        // broadcasts to every top-level window, so a real sign-out reaches it. The test sends
        // the message to every hidden, title-less top-level window of this thread, which is
        // that one (and nothing else that minds).
        const uint WmQueryEndSession = 0x0011;
        var endSessionLogoff = unchecked((nint)0x80000000u);
        var mainHwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var sent = new List<string>();
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            if (hwnd != mainHwnd && !IsWindowVisible(hwnd) && GetWindowTextLength(hwnd) == 0)
            {
                var answer = SendMessage(hwnd, WmQueryEndSession, 0, endSessionLogoff);
                sent.Add($"0x{hwnd:X}->{answer}");
            }

            return true;
        }, 0);
        await Task.Delay(300);
        Log($"  WM_QUERYENDSESSION sent to hidden thread windows [{string.Join(", ", sent)}]; our test handler cancelled the shutdown={cancelled}; window alive={window.IsLoaded}");
        Check(cancelled, "SessionEnding was raised for WM_QUERYENDSESSION");

        var after = SessionSnapshot.Load(AppPaths.SessionFile, out _);
        Log($"  file now: closeReason={after?.CloseReason} savedAt={after?.SavedAt:HH:mm:ss.fff} tabs={after?.Tabs.Count}");
        Check(after is { CloseReason: SessionCloseReason.SessionEnding }, "the file was written synchronously with closeReason=sessionEnding");
        Check(after?.Tabs.Count == 1 && after.Tabs[0].Label == "signed out", "it still carries the tabs");

        // Nothing is written after the closing save: a heartbeat must not turn it back into "running".
        tab.UserLabel = "after sign-out";
        await Task.Delay(2600);
        var later = SessionSnapshot.Load(AppPaths.SessionFile, out _);
        Check(later is { CloseReason: SessionCloseReason.SessionEnding } && later.Tabs[0].Label == "signed out", "later heartbeats did not overwrite the sign-out save");

        Log($"=== selftest (sessionend) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    private delegate bool EnumThreadWindowsProc(nint hwnd, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumThreadWindowsProc callback, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hwnd);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// The resume path end to end (§12.13): a first <c>opencode run</c> creates a session the
    /// plugin reports through <c>session.created</c>; a second <c>opencode run --session
    /// &lt;that id&gt;</c> is a new OpenCode process that never sees that event, so its plugin
    /// must adopt the id from the turn's own events — otherwise a restored tab could never be
    /// resumed by id again.
    /// </summary>
    /// <summary>
    /// The tab strip under crowding (§12.14): with room to spare tabs are 150-240 px; as tabs
    /// are added every tab shrinks to the same width down to 104 px; past that the strip
    /// scrolls, a chevron appears, the far edge fades, and activating a tab brings it into
    /// view. Measured at 4, 12 and 20 tabs, with a PNG of the strip at each.
    /// </summary>
    private static async Task RunOverflowAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (overflow) start ===");
        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        var strip = window.Strip;
        var caption = (System.Windows.FrameworkElement)window.FindName("TitleBarSurface")!;
        double TabWidth() => FindAll<System.Windows.Controls.Border>(strip).Where(b => b.Name == "TabRoot").Select(b => b.ActualWidth).DefaultIfEmpty(0).Average();
        int Visible() => FindAll<System.Windows.Controls.Border>(strip).Count(b => b.Name == "TabRoot" && b.ActualWidth > 0);
        var chevron = (System.Windows.Controls.Button)strip.FindName("BtnOverflow");
        var fadeRight = (System.Windows.FrameworkElement)strip.FindName("FadeRight");
        var fadeLeft = (System.Windows.FrameworkElement)strip.FindName("FadeLeft");

        async Task AddUntil(int count)
        {
            while (window.Tabs.Count < count)
            {
                window.AddTab(tab.Profile, activate: false);
                await Task.Delay(60);
            }

            await Task.Delay(700);
        }

        await AddUntil(4);
        Log($"  4 tabs: width={TabWidth():F0} overflow={strip.IsOverflowing} chevron={chevron.IsVisible} fades={fadeLeft.IsVisible}/{fadeRight.IsVisible} strip={strip.ActualWidth:F0} caption={caption.ActualWidth:F0}");
        Check(TabWidth() is >= 150 and <= 240 && !strip.IsOverflowing && !chevron.IsVisible, "4 tabs: relaxed widths, no overflow chrome");
        SaveVisual(caption, "overshell-selftest-overflow-04.png");

        await AddUntil(12);
        var w12 = TabWidth();
        Log($"  12 tabs: width={w12:F0} overflow={strip.IsOverflowing} chevron={chevron.IsVisible} fades={fadeLeft.IsVisible}/{fadeRight.IsVisible}");
        Check(w12 < 150 && w12 >= 104, "12 tabs: every tab shrank below 150 px but not below 104");
        Check(Visible() == 12, "…all twelve are laid out");
        SaveVisual(caption, "overshell-selftest-overflow-12.png");

        await AddUntil(20);
        var w20 = TabWidth();
        Log($"  20 tabs: width={w20:F0} overflow={strip.IsOverflowing} chevron={chevron.IsVisible} fades={fadeLeft.IsVisible}/{fadeRight.IsVisible} scrollable={((System.Windows.Controls.ScrollViewer)strip.FindName("Scroller")).ScrollableWidth:F0}");
        Check(Math.Abs(w20 - 104) < 1.5, "20 tabs: tabs stopped at the 104 px floor");
        Check(strip.IsOverflowing && chevron.IsVisible, "…the strip scrolls and the overflow chevron is shown");
        Check(fadeRight.IsVisible && !fadeLeft.IsVisible, "…the right edge fades (more tabs that way), the left does not (at the start)");
        SaveVisual(caption, "overshell-selftest-overflow-20-start.png");

        // Activating the last tab scrolls it into view; the fades swap sides.
        var scroller = (System.Windows.Controls.ScrollViewer)strip.FindName("Scroller");
        window.ActiveTab = window.Tabs[^1];
        await Task.Delay(700);
        var lastRoot = FindAll<System.Windows.Controls.Border>(strip).Last(b => b.Name == "TabRoot");
        var lastRight = lastRoot.TranslatePoint(new System.Windows.Point(lastRoot.ActualWidth, 0), scroller).X;
        Log($"  last tab active: offset={scroller.HorizontalOffset:F0}/{scroller.ScrollableWidth:F0} last tab right edge at {lastRight:F0} of viewport {scroller.ViewportWidth:F0} fades={fadeLeft.IsVisible}/{fadeRight.IsVisible}");
        Check(scroller.HorizontalOffset > 0 && lastRight <= scroller.ViewportWidth + 0.5, "activating the last tab scrolled it into view");
        Check(fadeLeft.IsVisible, "…and now the left edge fades (tabs scrolled out that way)");
        SaveVisual(caption, "overshell-selftest-overflow-20-end.png");

        window.ActiveTab = window.Tabs[0];
        await Task.Delay(700);
        Check(scroller.HorizontalOffset < 1, "activating the first tab scrolled back to the start");

        // The chevron opens the switcher.
        window.Commands.TryExecute("palette.tabs");
        await Task.Delay(600);
        var items = window.Palette?.FindName("List") is System.Windows.Controls.ListBox list ? list.Items.Count : -1;
        Check(items == 20 || window.Palette is null, $"the switcher (what the chevron opens) lists every tab ({items})");
        window.Palette?.Close();
        await Task.Delay(300);

        // Back to few tabs: relaxed again.
        foreach (var extra in window.Tabs.Skip(1).ToArray())
        {
            window.CloseTab(extra);
        }

        await Task.Delay(700);
        Check(TabWidth() >= 150 && !strip.IsOverflowing && !chevron.IsVisible, "closing them relaxes the widths and hides the chrome");
        Log($"=== selftest (overflow) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    /// <summary>
    /// Workspaces (§12.14): save the open tabs as one, see the file and its command appear,    /// open it (tabs added next to the open ones, command typed, label and group kept), and
    /// reach it through an <c>overshell://workspace/…</c> request. Runs against a throwaway
    /// configuration root (the launcher sets OVERSHELL_CONFIG_DIR).
    /// </summary>
    private static async Task RunWorkspacesAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (workspaces) start ===");
        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        await WaitForPromptAsync(tab);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Two tabs, one with a label and a group, one a "running agent" by report.
        tab.UserLabel = "ws build";
        window.SetGroup(tab, "ws group");
        var second = window.AddTab(tab.Profile with { StartingDirectory = windows }, activate: false);
        await Task.Delay(1200);
        second.ApplyReport(new IntegrationReport(second.Id, "opencode", 1, AgentState.Working, "opencode", "working", null, "ses-ws", null, Release: false));
        await Task.Delay(300);

        // ---- save ----
        var path = window.SaveWorkspace("Selftest WS");
        var text = System.IO.File.ReadAllText(path);
        Log($"  saved: {path}\n{text}");
        Check(System.IO.Path.GetFileName(path) == "selftest-ws.jsonc" && path.StartsWith(AppPaths.WorkspacesDir, StringComparison.OrdinalIgnoreCase), "workspace.save wrote workspaces\\selftest-ws.jsonc under the configuration root");
        Check(text.Contains("\"label\": \"ws build\"", StringComparison.Ordinal) && text.Contains("\"group\": \"ws group\"", StringComparison.Ordinal), "…with the label and group");
        Check(text.Contains("\"command\": \"opencode\"", StringComparison.Ordinal), "…and the agent tab's program as its command");
        Check(window.Commands.Find("workspace.open.selftest-ws") is not null, "the command workspace.open.selftest-ws exists right away");

        // ---- a file written by hand appears as a command on reload ----
        var byHand = System.IO.Path.Combine(AppPaths.WorkspacesDir, "by-hand.jsonc");
        System.IO.File.WriteAllText(byHand, $$"""{ "name": "By Hand", "tabs": [ { "profile": "{{tab.Profile.Name}}", "cwd": "{{windows.Replace("\\", "\\\\")}}", "label": "handmade", "group": "hand", "command": "Write-Host workspace-cmd-ran" } ] }""");
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && window.Commands.Find("workspace.open.by-hand") is null)
        {
            await Task.Delay(250);
        }

        Check(window.Commands.Find("workspace.open.by-hand") is not null, "a workspaces\\by-hand.jsonc saved by hand became a command without a restart (hot reload)");

        // ---- open: tabs added next to the open ones, the command typed once the shell is quiet ----
        var before = window.Tabs.Count;
        window.Commands.TryExecute("workspace.open.by-hand");
        await Task.Delay(500);
        var opened = window.Tabs.LastOrDefault();
        Check(window.Tabs.Count == before + 1 && opened is { UserLabel: "handmade", Group: "hand" } && ReferenceEquals(window.ActiveTab, opened), "opening added the tab next to the open ones, active, with its label and group");
        Check(string.Equals(opened?.WorkingDirectory?.TrimEnd('\\'), windows.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "…in the workspace's directory");
        Check(opened?.RestoreNote?.StartsWith("from workspace 'By Hand'", StringComparison.Ordinal) == true, "…with a note saying which workspace");

        var typed = false;
        deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !typed && opened is not null)
        {
            await Task.Delay(500);
            opened.RequestScreen();
            await Task.Delay(300);
            typed = opened.ScreenRows.Any(r => r.Contains("workspace-cmd-ran", StringComparison.Ordinal) && !r.Contains("Write-Host", StringComparison.Ordinal));
        }

        Check(typed, "the workspace's command was typed into the new shell and ran");

        // ---- protocol: a second start would hand this over; here the running window handles it ----
        before = window.Tabs.Count;
        window.HandleArguments([Core.Integrations.ProtocolRequest.WorkspaceUrl("Selftest WS")]);
        await Task.Delay(800);
        Log($"  after overshell://workspace/Selftest WS: tabs {before}->{window.Tabs.Count} labels=[{string.Join(", ", window.Tabs.Select(t => t.UserLabel ?? "-"))}]");
        Check(window.Tabs.Count == before + 2, "overshell://workspace/<name> opened the saved workspace's two tabs");
        Check(window.Tabs.Count(t => t.UserLabel == "ws build") == 2, "…the labelled one twice now (the original and the workspace's copy)");

        window.HandleArguments(["overshell://workspace/no-such"]);
        await Task.Delay(300);
        Check(((System.Windows.Controls.TextBlock)window.FindName("TxtMessage")!).Text.Contains("No workspace", StringComparison.Ordinal), "an unknown workspace name is a status message, not an error");

        foreach (var extra in window.Tabs.Skip(1).ToArray())
        {
            window.CloseTab(extra);
        }

        Log($"=== selftest (workspaces) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    /// <summary>
    /// The previous screen (§12.14): the launcher seeds an interrupted session whose tab has    /// saved rows with a marker; the restored tab must show those rows dimmed above its new
    /// prompt, with the rule naming when they were seen. The running tab's rows must land in
    /// the screens file within a few seconds, keyed by the tab's id.
    /// </summary>
    private static async Task RunGhostAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (ghost) start ===");
        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        await WaitForPromptAsync(tab);
        tab.RequestScreen();
        await Task.Delay(400);
        var rows = tab.ScreenRows;
        var marker = rows.ToList().FindIndex(r => r.Contains("GHOST-MARKER-LINE", StringComparison.Ordinal));
        var rule = rows.ToList().FindIndex(r => r.Contains("interrupted", StringComparison.Ordinal) && r.Contains("the screen before", StringComparison.Ordinal));
        var prompt = rows.ToList().FindLastIndex(r => r.Contains("PS ", StringComparison.Ordinal) && r.Contains('>'));
        Log($"  restored={window.RestoredSession} rows={rows.Count} marker@{marker} rule@{rule} prompt@{prompt}");
        Log($"  screen: {string.Join(" ⏎ ", rows.Where(r => r.Trim().Length > 0).Take(8))}");
        Check(window.RestoredSession, "the interrupted session was restored");
        Check(marker >= 0, "the previous screen's rows are on the new screen");
        Check(rule > marker, "…followed by the rule naming the interrupted session");
        Check(prompt > rule, "…and the shell's prompt below both");

        // The rows are dim text, not something the shell ran: the shell's own output is unaffected.
        tab.SendText("Write-Host ghost-live-output\r");
        await Task.Delay(1500);
        tab.RequestScreen();
        await Task.Delay(400);
        Check(tab.ScreenRows.Any(r => r.Contains("ghost-live-output", StringComparison.Ordinal) && !r.Contains("Write-Host", StringComparison.Ordinal)), "the shell works normally under the ghost");

        // The screens file for this run.
        await Task.Delay(6000);
        var screens = SessionScreens.Load(AppPaths.SessionScreensFile, out var error);
        var mine = screens?.Tabs.GetValueOrDefault(tab.Id);
        Log($"  screens file: tabs={screens?.Tabs.Count} mine={(mine is null ? "-" : $"{mine.Rows.Count} rows, last '{mine.Rows.LastOrDefault()}'")} error={error ?? "-"}");
        Check(mine is not null && mine.Rows.Any(r => r.Contains("ghost-live-output", StringComparison.Ordinal)), "the screens file carries this tab's current rows under its id");
        Check(screens is not null && !screens.Tabs.ContainsKey("ghost001"), "the previous run's tab id is gone from the file (it is this run's file now)");

        Log($"=== selftest (ghost) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    /// <summary>
    /// P5 batch 3 (§12.14): the close question while agents work, the restore note on a    /// restored tab, and the crash-loop guard. The launcher seeds the state root with a live
    /// file and archives that look like two early deaths, so the start must hold the restore.
    /// </summary>
    private static async Task RunResilienceAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (resilience) start ===");
        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        // ---- crash-loop guard: this start was seeded with two early interrupted runs ----
        var previous = window.PreviousSession;
        var archives = SessionHistory.List(AppPaths.StateRoot);
        Log($"  start: restored={window.RestoredSession} tabs={window.Tabs.Count} previous.started={previous?.StartedAt:HH:mm:ss} saved={previous?.SavedAt:HH:mm:ss} interrupted={previous?.Interrupted} archives={archives.Count} ({string.Join(", ", archives.Select(a => $"{(a.Interrupted ? "interrupted" : "closed")} {(a.SavedAt - (a.StartedAt ?? a.SavedAt)).TotalSeconds:F0}s"))})");
        Check(!window.RestoredSession && window.Tabs.Count == 1, "two early interrupted runs: the session was not restored (one default tab)");
        Check(archives.Any(a => a.Interrupted && a.Tabs.Count == 3), "…but the held session was archived first, so Session history brings it back");
        await Task.Delay(1500);
        var note = ((System.Windows.Controls.TextBlock)window.FindName("TxtMessage")!).Text;
        Log($"  status: '{note}'");
        Check(note.Contains("closed unexpectedly twice", StringComparison.Ordinal), "the status bar says why nothing was restored");

        // ---- restore note: reopen the held session from history, every tab says how it came back ----
        var held = archives.First(a => a.Interrupted && a.Tabs.Count == 3);
        var opened = window.ReopenSession(held);
        await Task.Delay(800);
        var notes = window.Tabs.Skip(1).Select(t => t.RestoreNote ?? "-").ToList();
        Log($"  reopened {opened}: notes = {string.Join(" | ", notes)}");
        Check(opened == 3 && notes.All(n => n.StartsWith("restored from ", StringComparison.Ordinal)), "every reopened tab carries a restore note");
        Check(notes.Any(n => n.Contains("typed `Write-Host resilience-resumed`", StringComparison.Ordinal)), "…the agent tab's note names the resume command and why");
        Check(notes.Any(n => n.Contains("directory only", StringComparison.Ordinal)), "…a plain shell's note says 'directory only'");
        Check(window.Tabs.Skip(1).Any(t => t.Tooltip.Contains("restored from", StringComparison.Ordinal)), "the note is in the tab tooltip");

        // ---- close question: an agent is Working, the close must ask ----
        var agentTab = window.Tabs.First(t => t.RestoreNote?.Contains("resilience-resumed", StringComparison.Ordinal) == true);
        await Task.Delay(3500); // the resume command types and runs
        agentTab.ApplyReport(new IntegrationReport(agentTab.Id, "selftest", 5, AgentState.Working, "selftest", "working", null, "ses-r", "Write-Host resilience-resumed", Release: false));
        await Task.Delay(300);
        MainWindow.AutoConfirmClose = false;
        window.Close();
        await Task.Delay(700);
        var picker = window.Palette;
        var items = picker?.FindName("List") is System.Windows.Controls.ListBox list ? list.Items.Cast<Chrome.PaletteItem>().ToList() : [];
        Log($"  close with {window.Tabs.Count(t => t.IsAgent && t.State == AgentState.Working)} working: window alive={window.IsLoaded} picker={picker?.IsVisible} items=[{string.Join(" | ", items.Select(i => i.Title))}]");
        Check(window.IsLoaded, "Close() with a working agent did not close the window");
        Check(picker is { IsVisible: true } && items.Count == 2 && items[0].Title.StartsWith("Close anyway - 1 agent working", StringComparison.Ordinal), "a question with 'Close anyway - 1 agent working' and 'Keep OverShell open' is on screen");
        if (picker is not null && items.Count == 2)
        {
            if (picker.Content is System.Windows.FrameworkElement root)
            {
                SaveVisual(root, "overshell-selftest-close-question.png");
            }

            items[1].Invoke();
            picker.Close();
            await Task.Delay(400);
            Check(window.IsLoaded, "'Keep OverShell open' kept it open");

            // Ask again and take the other answer: the window closes (the launcher sees a clean exit).
            window.Close();
            await Task.Delay(700);
            var again = window.Palette?.FindName("List") is System.Windows.Controls.ListBox list2 ? list2.Items.Cast<Chrome.PaletteItem>().ToList() : [];
            Check(again.Count == 2, "asking again shows the question again");
            Log($"=== selftest (resilience) result: {(pass && again.Count == 2 ? "ALL PASS" : "FAILED")} (closing through 'Close anyway') ===");
            if (again.Count == 2)
            {
                again[0].Invoke();
            }

            return;
        }

        MainWindow.AutoConfirmClose = true;
        Log($"=== selftest (resilience) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    /// <summary>
    /// The working directory from the process (§12.14). Part A runs in a <c>pwsh -NoProfile</c>    /// tab - a shell that announces nothing - so the probe alone is measured: a cd is followed
    /// within seconds (PowerShell from its prompt line, since its process directory never
    /// moves), a nested cmd's directory wins (PEB), leaving it goes back. Part B is the default
    /// profile: when the machine has the shell integration installed, the shell announces its
    /// directory (OSC 9;9) and the probe must defer to it for the shell itself while still
    /// following a nested, silent cmd.
    /// </summary>
    private static async Task RunCwdAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (cwd) start ===");
        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        static async Task<bool> WaitForCwdAsync(TerminalTab tab, string expected, int seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (string.Equals(tab.WorkingDirectory?.TrimEnd('\\'), expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                await Task.Delay(250);
            }

            return false;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.SystemDirectory;
        var temp = System.IO.Path.GetTempPath().TrimEnd('\\');

        // ---- A: a shell that never announces ----
        var bare = window.AddTab(first.Profile with { CommandLine = "pwsh.exe -NoLogo -NoProfile", StartingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }, activate: true);
        await WaitForPromptAsync(bare);
        Log($"  A start: cwd='{bare.WorkingDirectory}' announces={bare.AnnouncesDirectory}");

        var sw = Stopwatch.StartNew();
        bare.SendText($"cd '{windows}'\r");
        var followed = await WaitForCwdAsync(bare, windows, 8);
        Log($"  A after cd {windows}: cwd='{bare.WorkingDirectory}' in {sw.Elapsed.TotalSeconds:F1}s announces={bare.AnnouncesDirectory}");
        Check(!bare.AnnouncesDirectory, "A: -NoProfile pwsh announced nothing (the probe alone is at work)");
        Check(followed, "A: a cd with no shell integration was picked up (PowerShell: from its prompt line)");
        Check(sw.Elapsed < TimeSpan.FromSeconds(4), "A: …within four seconds");

        await WaitForPromptAsync(bare);
        bare.SendText($"cmd /k cd /d \"{system}\"\r");
        followed = await WaitForCwdAsync(bare, system, 8);
        Log($"  A nested cmd in {system}: cwd='{bare.WorkingDirectory}'");
        Check(followed, "A: a nested cmd's directory took over (deepest shell, from its process)");

        bare.SendText("exit\r");
        followed = await WaitForCwdAsync(bare, windows, 12);
        Log($"  A nested cmd exited: cwd='{bare.WorkingDirectory}'");
        Check(followed, "A: leaving the nested shell goes back to the outer shell's directory");

        await WaitForPromptAsync(bare);
        bare.SendText("$e=[char]27; $a=[char]7; cd $env:TEMP; Write-Host \"${e}]9;9;$env:TEMP${a}\"\r");
        followed = await WaitForCwdAsync(bare, temp, 4);
        Log($"  A OSC 9;9 {temp}: cwd='{bare.WorkingDirectory}' announces={bare.AnnouncesDirectory}");
        Check(followed && bare.AnnouncesDirectory, "A: an OSC 9;9 announcement set the directory and marked the shell as announcing");

        await WaitForPromptAsync(bare);
        bare.SendText($"cd '{windows}'\r");
        await Task.Delay(5000);
        Log($"  A silent cd after an announcement: cwd='{bare.WorkingDirectory}'");
        Check(string.Equals(bare.WorkingDirectory?.TrimEnd('\\'), temp, StringComparison.OrdinalIgnoreCase), "A: once the shell has announced, the probe defers to it for the shell's own directory");

        var trace = System.IO.File.Exists(TraceLog.Agents.Path) ? System.IO.File.ReadAllText(TraceLog.Agents.Path) : string.Empty;
        Check(trace.Contains($"[{bare.Id}] cwd: {windows} via prompt line of pwsh", StringComparison.OrdinalIgnoreCase) && trace.Contains($"[{bare.Id}] cwd: {system} via process cmd", StringComparison.OrdinalIgnoreCase), "A: the trace names the source of each directory (prompt line / process)");
        window.CloseTab(bare);

        // ---- B: the default profile, with whatever the machine has ----
        await WaitForPromptAsync(first);
        first.SendText($"cd '{windows}'\r");
        await WaitForCwdAsync(first, windows, 8);
        Log($"  B default profile: cwd='{first.WorkingDirectory}' announces={first.AnnouncesDirectory} (shell integration {(first.AnnouncesDirectory ? "installed" : "not installed")})");
        Check(string.Equals(first.WorkingDirectory?.TrimEnd('\\'), windows, StringComparison.OrdinalIgnoreCase), "B: the default profile's cd was followed, by announcement or by probe");

        if (first.AnnouncesDirectory)
        {
            await WaitForPromptAsync(first);
            first.SendText($"cmd /k cd /d \"{system}\"\r");
            followed = await WaitForCwdAsync(first, system, 8);
            Log($"  B nested cmd under an announcing shell: cwd='{first.WorkingDirectory}'");
            Check(followed, "B: a silent nested cmd is still followed by the probe while the announcing shell is quiet");
            first.SendText("exit\r");
            followed = await WaitForCwdAsync(first, windows, 12);
            Check(followed, "B: back to the announcing shell's directory when the nested shell exits");
        }
        else
        {
            Log("  SKIP  B: nested-cmd-under-announcing-shell (install the shell integration to exercise it)");
        }

        // What a restore would use: the directory the tab is in now, not where its shell started.
        window.SaveSession(force: true);
        var saved = SessionSnapshot.Load(AppPaths.SessionFile, out _);
        var savedCwd = saved?.Tabs.FirstOrDefault()?.WorkingDirectory;
        Log($"  session file: cwd='{savedCwd}'");
        Check(string.Equals(savedCwd?.TrimEnd('\\'), windows, StringComparison.OrdinalIgnoreCase), "the session file carries the followed directory, so a restore reopens there");

        Log($"=== selftest (cwd) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }
    /// <summary>Waits until the shell has printed a prompt and gone quiet for a second (at most 20 s).</summary>
    private static async Task WaitForPromptAsync(TerminalTab tab)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            tab.RequestScreen();
            await Task.Delay(500);
            var last = tab.ScreenRows.LastOrDefault(r => r.Trim().Length > 0) ?? string.Empty;
            if (tab.HasStarted && tab.IsRunning && (last.Contains("PS ", StringComparison.Ordinal) || last.TrimEnd().EndsWith('>')))
            {
                await Task.Delay(600);
                return;
            }
        }
    }

    /// <summary>
    /// P5 batch 1 ($112.14): duplicate tab, the detached mark as an icon, state icons stripped
    /// from displayed titles, and the status note reaching a layout without a status bar.
    /// </summary>
    private static async Task RunPolishAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (polish) start ===");
        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        // The shell must be at its prompt before anything is typed: input sent while a profile
        // is still loading lands wherever the shell is at that moment.
        await WaitForPromptAsync(tab);

        // ---- title: a state icon in front is detection's business, not the caption's ----
        // Raw ESC/BEL typed into PSReadLine would be keys (Escape clears the line), so the
        // sequences are built by the shell from [char] codes, as the main self-test does.
        var bell = char.ConvertFromUtf32(0x1F514);
        tab.SendText("$e=[char]27; $a=[char]7; Write-Host \"${e}]0;" + bell + " OC | OverShell | polish${a}\"; Start-Sleep -Seconds 3\r");
        await Task.Delay(1500);
        Log($"  title: raw harness={tab.Harness ?? "-"} shown='{tab.Title}' caption='{window.Title}' tooltip-first='{tab.Tooltip.Split('\n')[0]}'");
        Check(tab.Harness == "opencode", "the raw title (with the bell icon) still detects OpenCode");
        Check(tab.Title == "OC | OverShell | polish", "the shown title has the icon stripped");
        Check(!window.Title.Contains(bell, StringComparison.Ordinal) && window.Title.StartsWith("OC | OverShell | polish", StringComparison.Ordinal), "the window caption has it stripped too");
        Check(tab.Tooltip.Split('\n')[0] == "OC | OverShell | polish", "and the tooltip's first line");

        // ---- duplicate: same profile, same directory, same group, right after the source ----
        await Task.Delay(2000); // the title command's sleep ends
        await WaitForPromptAsync(tab);
        tab.SendText("cd $env:windir; Write-Host \"${e}]9;9;$env:windir${a}\"\r");
        await Task.Delay(1200);
        window.SetGroup(tab, "polish group");
        var third = window.AddTab(tab.Profile, activate: false);
        await Task.Delay(800);
        var before = window.Tabs.Count;
        window.ActiveTab = tab;
        window.Commands.TryExecute("tab.duplicate");
        await Task.Delay(1500);
        var dup = window.ActiveTab;
        Log($"  duplicate: tabs {before}->{window.Tabs.Count} source cwd='{tab.WorkingDirectory}' dup cwd='{dup?.WorkingDirectory}' group='{dup?.Group}' index source={window.Tabs.IndexOf(tab)} dup={window.Tabs.IndexOf(dup!)} third={window.Tabs.IndexOf(third)}");
        Check(window.Tabs.Count == before + 1 && dup is not null && !ReferenceEquals(dup, tab), "tab.duplicate opened a tab and activated it");
        Check(dup is not null && string.Equals(dup.WorkingDirectory?.TrimEnd('\\'), tab.WorkingDirectory?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "the duplicate starts in the source's current directory, not the profile's");
        Check(dup?.Group == "polish group", "the duplicate joined the source's group");
        Check(dup is not null && window.Tabs.IndexOf(dup) == window.Tabs.IndexOf(tab) + 1, "the duplicate sits right after its source");
        Check(window.HintFor("tab.duplicate") == "Ctrl+Shift+D" && window.HintFor("tab.new") == "Ctrl+Shift+T" && window.HintFor("tab.detach") == "Ctrl+Shift+X" && window.HintFor("tab.reopenClosed") == "Ctrl+Shift+Z", "Windows Terminal chords: Ctrl+Shift+T new, Ctrl+Shift+D duplicate; detach and reopen moved");

        // ---- detached mark: an icon element in the item, no text prefix ----
        var tearOff = window.Detach(third);
        await Task.Delay(600);
        var marks = FindAll<System.Windows.Controls.TextBlock>(window).Where(tb => tb.Name == "DetachedMark").ToList();
        var visibleMarks = marks.Where(m => m.IsVisible).ToList();
        Log($"  detached: third.Detached={third.Detached} detail='{third.Detail}' marks={marks.Count} visible={visibleMarks.Count} font={visibleMarks.FirstOrDefault()?.FontFamily}");
        Check(third.Detached && !third.Detail.Contains('\u29C9'), "the detail text no longer carries the U+29C9 prefix");
        Check(visibleMarks.Count == 1 && visibleMarks[0].FontFamily.Source.Contains("Segoe Fluent Icons", StringComparison.Ordinal), "exactly one tab item shows the detached mark, drawn from the icon font");
        if (window.FindName("TitleBarSurface") is System.Windows.FrameworkElement strip)
        {
            SaveVisual(strip, "overshell-selftest-polish-tabstrip.png");
        }

        if (tearOff is not null)
        {
            window.Attach(third);
            await Task.Delay(300);
        }

        Check(!FindAll<System.Windows.Controls.TextBlock>(window).Any(tb => tb.Name == "DetachedMark" && tb.IsVisible), "attaching hides the mark again");

        // ---- zen: the status note has nowhere to go but the overlay ----
        window.Commands.TryExecute("view.zen");
        await Task.Delay(800);
        var toastsBefore = window.Notifications?.VisibleToasts ?? -1;
        window.Commands.TryExecute("session.save");
        await Task.Delay(600);
        var toastsAfter = window.Notifications?.VisibleToasts ?? -1;
        Log($"  zen: status visible={window.CurrentLayout.Status.Visible} toasts {toastsBefore}->{toastsAfter}");
        Check(!window.CurrentLayout.Status.Visible, "the zen layout has no status bar");
        Check(toastsAfter == toastsBefore + 1, "the status message went to an in-window toast instead");
        if (window.Notifications?.ToastVisual is { } toasts)
        {
            SaveVisual(toasts, "overshell-selftest-polish-zen-toast.png");
        }

        window.Commands.TryExecute("view.terminal");
        await Task.Delay(500);
        window.CloseTab(dup!);
        window.CloseTab(third);
        Log($"=== selftest (polish) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    /// <summary>
    /// Harness icons (§12.13): one tab per bundled harness, made an agent by a report, then    /// the tab strip rendered at 1x and 3x. Each icon must resolve to a geometry, draw a
    /// plausible number of pixels in its cell, and differ from the others - the text glyphs
    /// they replaced rendered as near-identical smudges at 11 px.
    /// </summary>
    private static async Task RunIconsAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (icons) start ===");
        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        var harnesses = new[] { "opencode", "copilot", "claude", "codex", "generic" };
        var tabs = new List<TerminalTab> { tab };
        foreach (var _ in harnesses.Skip(1))
        {
            tabs.Add(window.AddTab(tab.Profile, activate: false));
        }

        await Task.Delay(1200);
        for (var i = 0; i < harnesses.Length; i++)
        {
            tabs[i].ApplyReport(new IntegrationReport(tabs[i].Id, harnesses[i], 1, AgentState.Idle, harnesses[i], "idle", null, null, null, Release: false));
            tabs[i].UserLabel = harnesses[i];
        }

        await Task.Delay(600);
        foreach (var t in tabs)
        {
            var geometry = t.IconGeometry;
            var bounds = geometry?.Bounds ?? System.Windows.Rect.Empty;
            Log($"  {t.Harness,-9} icon={(geometry is null ? "none" : $"geometry {bounds.Width:F0}x{bounds.Height:F0}")} textGlyph={t.ShowsTextGlyph} glyph='{t.Glyph}'");
            Check(geometry is not null && !t.ShowsTextGlyph, $"{t.Harness}: a vector icon resolved from the theme");
        }

        // Draw each icon alone on the theme's chrome colour, at the tab item's 11 px and at 3x,
        // and count the pixels that differ from the background - the shape's footprint.
        var fill = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Text.Secondary");
        var background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Surface.Chrome");
        var footprints = new Dictionary<string, int>();
        var strip = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = background };
        foreach (var t in tabs)
        {
            var cell = new System.Windows.Controls.Border { Width = 24, Height = 24, Background = background };
            var icon = new Chrome.HarnessIcon { Tab = t, Size = 11, Fill = fill, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            cell.Child = icon;
            strip.Children.Add(cell);
            footprints[t.Harness!] = CountInk(icon, 11, 3);
            Log($"  {t.Harness,-9} footprint at 3x: {footprints[t.Harness!]} px (11 px icon drawn at 33 px)");
            Check(footprints[t.Harness!] is > 120 and < 1000, $"{t.Harness}: the icon draws a shape, not a dot or a blob");
        }

        Check(footprints.Values.Distinct().Count() == footprints.Count, "the five icons have distinct footprints");

        strip.Measure(new System.Windows.Size(1000, 100));
        strip.Arrange(new System.Windows.Rect(strip.DesiredSize));
        strip.UpdateLayout();
        SaveVisualScaled(strip, "overshell-selftest-icons-1x.png", 1);
        SaveVisualScaled(strip, "overshell-selftest-icons-4x.png", 4);

        // The real tab strip, with the five agent tabs, as the user sees it.
        if (window.FindName("TitleBarSurface") is System.Windows.FrameworkElement titleBar)
        {
            SaveVisual(titleBar, "overshell-selftest-icons-tabstrip.png");
        }

        // tabs.showHarnessGlyph / tabs.twoLine were defined and never read (pre-P4): both must
        // now take effect on the live items - here through the static the window sets on reload.
        var hadIcon = tabs[0].ShowsHarnessIcon;
        TerminalTab.TabSettings = new Core.Settings.TabSettings { ShowHarnessGlyph = false, TwoLine = false };
        foreach (var t in tabs)
        {
            t.TabSettingsChanged();
        }

        await Task.Delay(300);
        var iconElements = FindAll<Chrome.HarnessIcon>(window).Where(i => i.Tab is not null && tabs.Contains(i.Tab)).ToList();
        var detailElements = FindAll<System.Windows.Controls.TextBlock>(window).Where(tb => tb.Name == "TabDetail").ToList();
        Log($"  toggles off: ShowsHarnessIcon={tabs[0].ShowsHarnessIcon} (was {hadIcon}) icons visible={iconElements.Count(i => i.Visibility == System.Windows.Visibility.Visible)}/{iconElements.Count} detail lines visible={detailElements.Count(d => d.Visibility == System.Windows.Visibility.Visible)}/{detailElements.Count}");
        Check(hadIcon && !tabs[0].ShowsHarnessIcon, "tabs.showHarnessGlyph=false hides the harness icon on agent tabs");
        Check(iconElements.Count > 0 && iconElements.All(i => i.Visibility != System.Windows.Visibility.Visible), "every tab item's icon element collapsed");
        Check(detailElements.Count > 0 && detailElements.All(d => d.Visibility != System.Windows.Visibility.Visible), "tabs.twoLine=false collapsed every tab item's detail line");
        if (window.FindName("TitleBarSurface") is System.Windows.FrameworkElement titleBarOff)
        {
            SaveVisual(titleBarOff, "overshell-selftest-icons-tabstrip-toggles-off.png");
        }

        TerminalTab.TabSettings = new Core.Settings.TabSettings();
        foreach (var t in tabs)
        {
            t.TabSettingsChanged();
        }

        await Task.Delay(200);
        Check(iconElements.All(i => i.Visibility == System.Windows.Visibility.Visible) && detailElements.All(d => d.Visibility == System.Windows.Visibility.Visible), "both back on: icons and detail lines visible again");

        foreach (var t in tabs.Skip(1))
        {
            window.CloseTab(t);
        }

        Log($"=== selftest (icons) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    private static IEnumerable<T> FindAll<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in FindAll<T>(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>Pixels of <paramref name="element"/> at <paramref name="scale"/> that are not fully transparent, after a layout at <paramref name="size"/>.</summary>
    private static int CountInk(System.Windows.FrameworkElement element, double size, int scale)
    {
        element.Measure(new System.Windows.Size(size, size));
        element.Arrange(new System.Windows.Rect(0, 0, size, size));
        element.UpdateLayout();
        var px = (int)Math.Ceiling(size * scale);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(px, px, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[px * px * 4];
        bitmap.CopyPixels(pixels, px * 4, 0);
        var ink = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 64)
            {
                ink++;
            }
        }

        return ink;
    }

    private static void SaveVisualScaled(System.Windows.FrameworkElement element, string fileName, int scale)
    {
        try
        {
            var width = (int)Math.Ceiling(element.ActualWidth * scale);
            var height = (int)Math.Ceiling(element.ActualHeight * scale);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
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

    /// <summary>
    /// History (§12.13): a closed tab is remembered with its directory and agent session and
    /// comes back through <c>tab.reopenClosed</c> with the agent resumed; the picker lists
    /// closed tabs and archived sessions; an archived session is reopened next to the open tabs.
    /// Runs against a state root the launcher seeded with one archive.
    /// </summary>
    private static async Task RunHistoryAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (history) start ===");
        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        var second = window.AddTab(tab.Profile, activate: false);
        await Task.Delay(1500);
        second.UserLabel = "to be closed";
        second.ApplyReport(new IntegrationReport(second.Id, "selftest", 1, AgentState.Working, "selftest", "working", null, "ses-history", "Write-Host history-resumed", Release: false));
        await Task.Delay(300);
        Log($"  before close: tabs={window.Tabs.Count} second label='{second.UserLabel}' agent={second.IsAgent} resume='{second.ResumeCommand}' cwd='{second.WorkingDirectory}'");

        var archivesBefore = SessionHistory.List(AppPaths.StateRoot);
        Log($"  archives before: {archivesBefore.Count} ({string.Join("; ", archivesBefore.Select(a => $"{System.IO.Path.GetFileName(a.Path)}: {a.Tabs.Count} tab(s)"))})");

        // ---- close, remember ----
        window.CloseTab(second);
        await Task.Delay(300);
        var remembered = window.RecentlyClosed.LastOrDefault();
        Log($"  recently closed: {window.RecentlyClosed.Count} entr(ies); last label='{remembered?.Label}' agentRunning={remembered?.AgentRunning} resume='{remembered?.ResumeCommand}' closedAt={remembered?.ClosedAt:HH:mm:ss}");
        Check(window.Tabs.Count == 1, "the tab closed");
        Check(remembered is { Label: "to be closed", AgentRunning: true, ResumeCommand: "Write-Host history-resumed" }, "the closed tab was remembered with its label and agent session");
        Check(window.Commands.Find("tab.reopenClosed")?.IsEnabled == true, "tab.reopenClosed is enabled while something is remembered");

        // ---- the file carries it (within the two-second save) ----
        await Task.Delay(2600);
        var onDisk = SessionSnapshot.Load(AppPaths.SessionFile, out _);
        Check(onDisk?.RecentlyClosed.Count == 1 && onDisk.RecentlyClosed[0].Label == "to be closed", "the session file carries the recently closed entry");

        // ---- picker ----
        var items = window.BuildHistoryItems(PaletteQuery.Parse(string.Empty));
        Log($"  picker items: {string.Join(" | ", items.Select(i => $"{i.Title} [{i.Detail}]"))}");
        Check(items.Count >= 2 && items[0].Title == "to be closed", "the picker lists the closed tab first");
        Check(items.Any(i => i.Title.StartsWith("Session ", StringComparison.Ordinal) && i.Detail!.Contains("tab", StringComparison.Ordinal)), "the picker lists the archived session with its tab summary");
        var filtered = window.BuildHistoryItems(PaletteQuery.Parse("interrupted"));
        Check(filtered.All(i => i.Title.Contains("interrupted", StringComparison.OrdinalIgnoreCase) || i.Detail!.Contains("interrupted", StringComparison.OrdinalIgnoreCase)), "typing filters the picker");

        window.Commands.TryExecute("session.history");
        await Task.Delay(600);
        var picker = window.Palette;
        var pickerItems = picker?.FindName("List") is System.Windows.Controls.ListBox list ? list.Items.Count : -1;
        Log($"  picker window: open={picker?.IsVisible} items={pickerItems}");
        if (picker is null)
        {
            Log("  SKIP  picker window check: it lost activation to another process while open");
        }
        else
        {
            Check(picker.IsVisible && pickerItems == items.Count, "session.history opened the picker with those items");
            picker.Close();
            await Task.Delay(400);
        }

        // ---- reopen: the tab comes back, the agent resumes ----
        window.Commands.TryExecute("tab.reopenClosed");
        await Task.Delay(300);
        var reopened = window.Tabs.LastOrDefault();
        Check(window.Tabs.Count == 2 && reopened is { UserLabel: "to be closed" } && ReferenceEquals(window.ActiveTab, reopened), "tab.reopenClosed reopened it, activated, with its label");
        Check(window.RecentlyClosed.Count == 0, "the entry left the recently-closed list");
        Check(reopened?.ResumeCommand == "Write-Host history-resumed" && reopened.Agent.SessionId == "ses-history", "the reopened tab knows its session id and resume command before any report");

        var typed = false;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !typed && reopened is not null)
        {
            await Task.Delay(500);
            reopened.RequestScreen();
            await Task.Delay(300);
            typed = reopened.ScreenRows.Any(r => r.Contains("history-resumed", StringComparison.Ordinal) && !r.Contains("Write-Host", StringComparison.Ordinal));
        }

        Check(typed, "the resume command was typed into the reopened tab and ran");

        // ---- reopen an archived session next to the open tabs ----
        var archive = SessionHistory.List(AppPaths.StateRoot).FirstOrDefault();
        if (archive is null)
        {
            Check(false, "an archived session exists to reopen (the launcher seeds one)");
        }
        else
        {
            var before = window.Tabs.Count;
            var opened = window.ReopenSession(archive);
            await Task.Delay(500);
            Log($"  reopened archive {System.IO.Path.GetFileName(archive.Path)}: {opened} tab(s); tabs now {window.Tabs.Count}; labels=[{string.Join(", ", window.Tabs.Select(t => t.UserLabel ?? "-"))}]");
            Check(opened == archive.Tabs.Count && window.Tabs.Count == before + opened, "every tab of the archived session was added next to the open ones");
            Check(window.Tabs.Skip(before).All(t => archive.Tabs.Any(a => a.Label == t.UserLabel)), "the reopened tabs carry the archive's labels");
        }

        Log($"=== selftest (history) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    private static async Task RunOpenCodeResumeAsync(MainWindow window, TerminalTab tab)
    {
        Log("=== selftest (opencode-resume) start ===");
        var started = Stopwatch.GetTimestamp();
        var pass = true;
        void Check(bool ok, string what)
        {
            pass &= ok;
            Log($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }

        async Task<(string? SessionId, string? Source, bool Released)> RunTurnAsync(string command)
        {
            tab.SendText(command + "\r");
            string? sessionId = null, source = null;
            var sawWorking = false;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000);
                sessionId ??= tab.Agent.SessionId;
                source ??= tab.Agent.AuthoritySource;
                sawWorking |= tab.State == AgentState.Working;
                if (source is not null && sessionId is not null && sawWorking && tab.State is AgentState.Done or AgentState.Idle)
                {
                    break;
                }
            }

            Log($"  +{Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s turn done: state={tab.State} source={source ?? "-"} session={sessionId ?? "-"} working-seen={sawWorking}");

            // The process probe notices opencode.exe is gone and releases the plugin, which clears the id.
            var releaseDeadline = DateTime.UtcNow.AddSeconds(16);
            var released = false;
            while (DateTime.UtcNow < releaseDeadline && !released)
            {
                await Task.Delay(500);
                released = tab.Agent.Authority == AgentAuthority.Detector && tab.Harness is null;
            }

            return (sessionId, source, released);
        }

        var first = await RunTurnAsync("opencode run \"Reply with exactly the word pong and nothing else.\"");
        Check(first.Source == "opencode" && first.SessionId is not null, $"first run: the plugin reported a session id ({first.SessionId ?? "-"})");
        Check(first.Released, "first run: the plugin was released once opencode.exe was gone");
        Check(tab.Agent.SessionId is null, "after release the tab holds no session id");

        if (first.SessionId is null)
        {
            Log("=== selftest (opencode-resume) result: FAILED (no session to resume) ===");
            return;
        }

        var second = await RunTurnAsync($"opencode run --session {first.SessionId} \"Reply with exactly the word pong again.\"");
        Check(second.Source == "opencode", "resumed run: the plugin reported again");
        Check(second.SessionId == first.SessionId, $"resumed run: the plugin adopted the resumed session's id without a session.created event ({second.SessionId ?? "-"})");
        Check(tab.ResumeCommand == $"opencode --session {first.SessionId}", $"the tab's resume command names that session: {tab.ResumeCommand ?? "-"}");

        var rows = await new Agents.ScreenReader().ReadRowsAsync(MainWindow.FindTerminalHwnd(tab.View));
        Log($"  screen tail: {string.Join(" ⏎ ", (rows ?? []).TakeLast(6))}");
        Log($"=== selftest (opencode-resume) result: {(pass ? "ALL PASS" : "FAILED")} ===");
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
        Check(window.DispatchChord(System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift), "Ctrl+Shift+T resolves through keybindings.jsonc to tab.new");
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
            if (palette is null)
            {
                // It opened and closed itself, and only Deactivated does that: another process took
                // the foreground for a moment (the machine is in use). Closing an owned window hands
                // activation back to the owner, so the foreground looks ours again by now.
                Log("  SKIP  palette focus checks: the palette lost activation to another process while open");
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
            System.IO.File.WriteAllText(OverShell.Core.AppPaths.KeybindingsFile, """[ { "keys": "ctrl+alt+9", "command": "tab.new" }, { "keys": "ctrl+shift+t", "command": "unbound" } ]""");
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
            Check(!window.DispatchChord(System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift), "…and \"unbound\" removed Ctrl+Shift+T");
            window.CloseTab(window.Tabs[^1]);

            System.IO.File.Delete(System.IO.Path.Combine(OverShell.Core.AppPaths.LayoutsDir, "top.jsonc"));
            System.IO.File.Delete(OverShell.Core.AppPaths.KeybindingsFile);
            await Task.Delay(1500);
            Log($"  after removing overrides: layout tabs={window.CurrentLayout.Tabs.Placement} caption={window.CaptionHeight}");
            Check(window.CurrentLayout.Tabs.Placement == OverShell.Core.Layout.TabsPlacement.Top && window.CaptionHeight > 50, "deleting the override restores the preset live");
            Check(window.DispatchChord(System.Windows.Input.Key.T, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift), "…and Ctrl+Shift+T is bound again");
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
        if (explain is null)
        {
            Log("  SKIP  explain checks: the panel lost activation to another process while open");
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

        // ---- 14. P3: Codex notify shim, native toast, tear-off ----
        // The Codex shim exactly as installed (script from the embedded resource, JSON as the
        // last argument), run from the second tab, which nothing has claimed.
        var codexScript = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overshell-selftest-codex-notify.ps1");
        System.IO.File.WriteAllText(codexScript, IntegrationInstaller.CodexScriptContent());
        window.ActiveTab = second;
        await Task.Delay(300);
        var codexJson = "{\"type\":\"agent-turn-complete\",\"thread-id\":\"thr-selftest\",\"turn-id\":\"t1\",\"cwd\":\"C:\\\\x\",\"input-messages\":[\"hi\"],\"last-assistant-message\":\"All done here.\"}";
        second.SendText($"& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File '{codexScript}' '{codexJson}'; Write-Host selftest-codex-done\r");
        var codexDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < codexDeadline && second.Agent.SessionId != "thr-selftest")
        {
            await Task.Delay(250);
        }

        Log($"  codex notify: session={second.Agent.SessionId} state={second.State} authority={second.Agent.Authority} harness={second.Harness} summary='{second.Summary}' resume='{second.ResumeCommand}' explain='{second.Agent.Explain}'");
        Check(second.Agent.SessionId == "thr-selftest" && second.Harness == "codex", "the Codex notify script posted the turn-complete payload and the tab became a Codex agent");
        Check(second.Agent.Authority == AgentAuthority.Detector, "…without taking authority (advisory)");
        Check(second.ResumeCommand == "codex resume thr-selftest" && second.Summary == "All done here.", "…recording the thread for resume and the last message as the summary");
        System.IO.File.Delete(codexScript);

        // The native toast sink: WinRT through hand-written COM. The shell's history is read
        // back by Windows PowerShell (which can call WinRT) after the run, in the run script.
        var toastResult = Notifications.NativeToast.Show("OverShell self-test", "toast sink check", "you can dismiss this", OverShell.Core.Integrations.ProtocolRequest.FocusUrl(tab.Id), "overshell-selftest", "overshell", silent: true);
        Log($"  toast: {(toastResult ?? "shown")}");
        Check(toastResult is null, "the native toast was accepted by the notification platform");

        // Tear-off: the live surface moves to a second window and back — same HWND, session alive.
        window.ActiveTab = tab;
        await Task.Delay(300);
        var hwndBefore = second.TerminalHwnd;
        var tearOff = window.Detach(second);
        await Task.Delay(800);
        var tearOffHwnd = tearOff is null ? 0 : new System.Windows.Interop.WindowInteropHelper(tearOff).Handle;
        Log($"  detach: window=0x{tearOffHwnd:X} visible={tearOff?.IsVisible} detached={second.Detached} view-parent={second.View.Parent?.GetType().Name} hwnd before=0x{hwndBefore:X} after=0x{second.TerminalHwnd:X} main-active={window.ActiveTab?.Id} tabs={window.Tabs.Count}");
        Check(tearOff is { IsVisible: true } && second.Detached && !ReferenceEquals(second.View.Parent, window.FindName("TerminalHost")), "tab.detach moved the surface into its own window");
        Check(second.TerminalHwnd == hwndBefore && second.IsRunning, "same terminal HWND, session alive");
        Check(window.Tabs.Contains(second) && ReferenceEquals(window.ActiveTab, tab), "the tab stays in the collection; the main window shows its neighbour");

        second.SendText("Write-Host selftest-tearoff-ok\r");
        await Task.Delay(1500);
        second.RequestScreen();
        await Task.Delay(400);
        Check(second.ScreenRows.Any(r => r.Trim() == "selftest-tearoff-ok"), "output written in the tear-off shows on its screen (UIA reads the moved HWND)");

        window.Attach(second);
        await Task.Delay(600);
        Log($"  attach: detached={second.Detached} view-parent={second.View.Parent?.GetType().Name} tear-offs={window.TearOffs.Count} active={window.ActiveTab?.Id} hwnd=0x{second.TerminalHwnd:X}");
        Check(!second.Detached && ReferenceEquals(second.View.Parent, window.FindName("TerminalHost")) && window.TearOffs.Count == 0, "tab.attach brought the surface back and closed the window");
        Check(ReferenceEquals(window.ActiveTab, second) && second.TerminalHwnd == hwndBefore && second.IsRunning, "…active again, same HWND, still alive");

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
