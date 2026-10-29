using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using OverShell.Core;
using OverShell.Core.Extensibility;
using OverShell.Core.Integrations;
using OverShell.Core.Settings;
using OverShell.Core.Tabs;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunScratchAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (scratch) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var root = Path.Combine(AppPaths.StateRoot, "scratch processes");
        Directory.CreateDirectory(root);
        var initial = window.Tabs.ToHashSet();
        var observed = new List<(string Id, string? Alias, bool Scratch)>();
        void Opened(ITab tab) => observed.Add((tab.Id, tab.Alias, tab.Lifetime.Scratch));
        window.Shell.TabOpened += Opened;
        try
        {
            window.Commands.TryExecute("tab.newFloating");
            var normal = window.FloatingWindows.Single().Tab;
            Check(normal.Lifetime == TabLifetime.Standard && !normal.IsScratch, "New floating tab creates an ordinary restorable tab");
            Check(await TransferUntilAsync(() => normal.IsRunning), "the new floating tab starts a real shell");
            window.Presentations.HideFloating(normal.Id);
            var scratch = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, Alias: "scratch-live", Scratch: true, Floating: true))!;
            Check(scratch.IsScratch && !scratch.Lifetime.Restore && !scratch.Lifetime.History && scratch.Floating, "scratch creation applies independent presentation and transient retention defaults");
            Check(scratch.WorkingDirectory == first.WorkingDirectory, "scratch creation inherits the invoking directory");
            Check(observed.Any(t => t.Id == scratch.Id && t.Alias == "scratch-live" && t.Scratch), "extensions observe fully initialized alias and lifetime metadata at TabOpened");
            Check(await TransferUntilAsync(() => scratch.IsRunning), "the scratch shell is running");
            var scratchPid = scratch.Session.ProcessId!.Value;
            window.Presentations.HideFloating(scratch.Id);
            Check(scratch.IsRunning && window.Tabs.Contains(scratch), "Hide keeps a scratch terminal discoverable and running");
            var later = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, Label: "later persistent", Activate: true))!;
            window.Commands.TryExecute("session.save");
            var saved = SessionSnapshot.Load(AppPaths.SessionFile, out _)!;
            Check(saved.Tabs.All(t => t.Id != scratch.Id) && saved.Tabs.Any(t => t.Id == normal.Id), "session snapshots include normal floats and exclude default scratch tabs");
            Check(saved.Tabs[saved.ActiveIndex].Id == later.Id, "saved active index uses the filtered retained list, not the original tab index");
            var historyBefore = saved.RecentlyClosed.Count;
            window.CloseTab(scratch);
            window.Commands.TryExecute("session.save");
            Check(SessionSnapshot.Load(AppPaths.SessionFile, out _)!.RecentlyClosed.Count == historyBefore, "default scratch termination does not add closed-tab history");
            Check(await TransferUntilAsync(() => !Running(scratchPid)), "Terminate actually exits the scratch process");
            window.ActiveTab = first;

            var kept = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, Scratch: true, Floating: true, Alias: "keep-me"))!;
            window.ExecuteForTab("tab.keep", kept);
            Check(kept.Lifetime == TabLifetime.Standard && kept.Alias == "keep-me" && kept.Floating, "Keep promotes retention without changing runtime identity or presentation");
            Check(await TransferUntilAsync(() => kept.IsRunning), "the promoted shell remains alive");
            window.CloseTab(kept);
            window.Commands.TryExecute("session.save");
            Check(SessionSnapshot.Load(AppPaths.SessionFile, out _)!.RecentlyClosed.Any(t => t.Id == kept.Id), "promoted tabs participate in closed-tab history");

            var successFile = Path.Combine(root, "success Ω.txt");
            var success = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, WorkingDirectory: root, Scratch: true, Floating: true,
                Process: Script($"[IO.File]::AppendAllText('{Ps(successFile)}','x',[Text.UTF8Encoding]::new($false)); Write-Host SUCCESS_OUTPUT; exit 0")))!;
            Check(await TransferUntilAsync(() => success.HasStarted && !success.IsRunning && success.ExitCode == 0), "a one-shot process reports its real successful exit");
            Check(File.ReadAllText(successFile) == "x" && window.Tabs.Contains(success) && success.Floating, "default exit policy retains one-shot output and the float");
            Check((await window.Search.FindAsync(success.TerminalHwnd, "SUCCESS_OUTPUT", true, null))?.Matches.Count > 0, "completed process output remains readable in the live terminal buffer");
            Check(success.IsOneShot && !success.ShellIntegration.Injected, "raw one-shot launches bypass interactive shell-integration injection");
            window.ExecuteForTab("tab.keep", success);
            window.Commands.TryExecute("session.save");
            var savedSuccess = SessionSnapshot.Load(AppPaths.SessionFile, out _)!.Tabs.Single(t => t.Id == success.Id);
            Check(!savedSuccess.AgentRunning && savedSuccess.ResumeCommand is null, "retained one-shot commands are never serialized as automatic resume commands");
            window.Presentations.HideFloating(success.Id);
            var restored = window.OpenSavedTab(savedSuccess, activate: false)!;
            Check(await TransferUntilAsync(() => restored.IsRunning) && restored.Profile.CommandLine == first.Profile.CommandLine && File.ReadAllText(successFile) == "x", "restoring a kept one-shot tab opens its profile without replaying the command");
            window.CloseTab(restored);
            var duplicate = window.DuplicateTab(success);
            Check(await TransferUntilAsync(() => duplicate.IsRunning) && !duplicate.IsOneShot && duplicate.Alias is null && File.ReadAllText(successFile) == "x",
                "duplicating a completed job opens the original profile rather than replaying the one-shot command");
            window.CloseTab(duplicate);
            window.ActiveTab = first;

            var auto = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, Scratch: true, Floating: true, Alias: "one-shot-auto",
                Process: Script("Write-Host AUTO_SUCCESS; exit 0"), OnExit: TabExitBehavior.CloseOnSuccess))!;
            Check(await TransferUntilAsync(() => !window.Tabs.Contains(auto)), "closeOnSuccess removes a successfully exited one-shot tab");
            Check(window.Addresses.Resolve(new TabSelector(Alias: "one-shot-auto")).TabId is null && window.ActiveTab == first, "automatic close releases the alias and restores the original work");
            var failure = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, Scratch: true, Floating: true,
                Process: Script("Write-Host FAILURE_OUTPUT; exit 7"), OnExit: TabExitBehavior.CloseOnSuccess))!;
            Check(await TransferUntilAsync(() => failure.HasStarted && !failure.IsRunning && failure.ExitCode == 7) && window.Tabs.Contains(failure), "closeOnSuccess retains failed command output with its exit code");
            window.Presentations.HideFloating(failure.Id);
            var always = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, Scratch: true,
                Process: Script("exit 9"), OnExit: TabExitBehavior.CloseAlways, Activate: false))!;
            Check(await TransferUntilAsync(() => !window.Tabs.Contains(always)), "closeAlways also closes failed processes");
            var launchFailure = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, Scratch: true, Activate: false,
                Process: new ProcessLaunch("overshell-missing-executable-9483.exe"), OnExit: TabExitBehavior.CloseOnSuccess))!;
            Check(await TransferUntilAsync(() => launchFailure.State == Core.Agents.AgentState.Exited) && window.Tabs.Contains(launchFailure) && !launchFailure.HasStarted,
                "a failed process launch is retained rather than mistaken for success");

            var count = window.Tabs.Count;
            var control = new ShellControl(window.Shell);
            var bad = await control.OpenAsync(JsonNode.Parse("""{"scratch":true,"process":{"executable":"cmd.exe"},"command":"dir"}""")!.AsObject());
            Check(bad.Result.Status == ControlStatus.BadRequest && window.Tabs.Count == count, "invalid combined process/command creation is rejected atomically");
            var background = await control.OpenAsync(JsonNode.Parse("""
                {"scratch":true,"presentation":"floating","activate":false,"alias":"background-created"}
                """)!.AsObject());
            var backgroundId = background.Tab?["id"]?.GetValue<string>();
            Check(background.Result.Status == ControlStatus.Ok && backgroundId is not null && window.Shell.FocusedTab == first, "API activate:false creates a floating terminal without taking input focus");
            Check((await control.LifetimeAsync("alias:background-created", JsonNode.Parse("""{"scratch":false,"restore":true,"history":true,"onExit":"keep"}""")!.AsObject())).Status == ControlStatus.Ok,
                "control callers can promote a scratch tab through the lifetime API");
            if (backgroundId is not null) window.CloseTab((TerminalTab)window.Shell.Find(backgroundId)!);

            var argFile = Path.Combine(root, "argv Ω.json");
            var scriptFile = Path.Combine(root, "capture arguments.ps1");
            File.WriteAllText(scriptFile, "param([string]$Destination)\n[IO.File]::WriteAllText($Destination,(ConvertTo-Json -InputObject @($args) -Compress),[Text.UTF8Encoding]::new($false))\n", new UTF8Encoding(true));
            string[] arguments = ["", "space arg", "quote\"arg", @"C:\trailing slash\", "%PATH%", "Ω"];
            var argv = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, Scratch: true, Activate: false,
                Process: new ProcessLaunch("powershell.exe", new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptFile, argFile }.Concat(arguments).ToArray())))!;
            var produced = await TransferUntilAsync(() => File.Exists(argFile));
            Check(produced, "the real process argv probe produced output");
            if (produced)
            {
                var received = JsonNode.Parse(File.ReadAllText(argFile))!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
                Check(received.SequenceEqual(arguments), "empty arguments, spaces, quotes, trailing slashes, literal environment tokens and Unicode survive real process launch");
            }
            else
            {
                argv.RequestScreen();
                await Task.Delay(350);
                Log($"  argv probe exit={argv.ExitCode}: {string.Join(" | ", argv.ScreenRows)}");
            }

            window.ActiveTab = first;
            window.Commands.TryExecute("tab.scratch.command");
            await Task.Delay(100);
            var prompt = Application.Current.Windows.OfType<Chrome.PaletteWindow>().Single(w => w.IsActive);
            var pickerFile = Path.Combine(root, "picker command.txt");
            ProfileInput(prompt).Text = $"[IO.File]::WriteAllText('{Ps(pickerFile)}','from picker'); exit 0";
            ProfileKey(prompt, Key.Return);
            Check(await TransferUntilAsync(() => File.Exists(pickerFile)), "the command picker launches a real one-shot command with the configured shell");
        }
        finally
        {
            window.Shell.TabOpened -= Opened;
            foreach (var tab in window.Tabs.Where(t => !initial.Contains(t)).ToArray()) window.CloseTab(tab);
            window.ActiveTab = first;
        }
        Log($"=== selftest (scratch) result: {(pass ? "ALL PASS" : "FAILED")} ===");
        static string Ps(string value) => value.Replace("'", "''", StringComparison.Ordinal);
        static ProcessLaunch Script(string text) => new("powershell.exe", ["-NoLogo", "-NoProfile", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(text))]);
        static bool Running(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    }
}
