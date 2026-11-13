using System.IO;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;
using OverShell.Core.Settings;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunDetailsRestartAsync(MainWindow window, TerminalTab first)
    {
        Log($"=== selftest ({Mode}) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var expectedFile = Path.Combine(AppPaths.StateRoot, "details-expected.json");
        if (Mode == "details1")
        {
            var tabs = new List<TerminalTab>();
            foreach (var name in new[] { "alpha", "beta" })
            {
                var cwd = Directory.CreateDirectory(Path.Combine(AppPaths.StateRoot, "restart-projects", name)).FullName;
                Directory.CreateDirectory(Path.Combine(cwd, ".git"));
                File.WriteAllText(Path.Combine(cwd, ".git", "HEAD"), $"ref: refs/heads/{name}-main\n");
                tabs.Add(window.AddTab(first.Profile with { StartingDirectory = cwd }, activate: false));
            }
            Check(await TransferUntilAsync(() => tabs.All(t => t.IsRunning && t.AnnouncesDirectory)), "both crash-fixture shells announce their own project directories");
            window.CloseTab(first);
            foreach (var tab in tabs)
                tab.ApplyReport(new IntegrationReport(tab.Id, "restart-fixture", 1, AgentState.Idle, "selftest", null, null,
                    "session-" + tab.Project, "Write-Host details-resume-ready", false,
                    SessionTitle: tab.Project + " task", SessionKnown: true, WorkingDirectory: tab.WorkingDirectory));
            window.SaveSession(force: true);
            var snapshot = SessionSnapshot.Load(AppPaths.SessionFile, out _)!;
            Check(snapshot.Interrupted && snapshot.Tabs.Count == 2 && snapshot.Tabs.Select(t => t.Project).SequenceEqual(new[] { "alpha", "beta" }),
                "the checkpoint retains two independent project/session pairs before abrupt termination");
            File.WriteAllText(expectedFile, Jsonc.Serialize(snapshot.Tabs));
            var unrelated = Directory.CreateDirectory(Path.Combine(AppPaths.StateRoot, "restart-projects", "Shubbak")).FullName;
            File.WriteAllText(Path.Combine(AppPaths.StateRoot, "details-profiles.json"), Jsonc.Serialize(new
            {
                defaultProfile = first.Profile.Id,
                profiles = new { list = new[] { new { guid = first.Profile.Id, name = first.Profile.Name, commandline = first.Profile.CommandLine, startingDirectory = unrelated } } },
            }));
            Check(window.Tabs.Select(t => t.Badge?.Text).SequenceEqual(new[] { "1", "2" }), "automatic hints exist before the crash without entering saved tab metadata");
        }
        else
        {
            var expected = Jsonc.To<List<SavedTab>>(Jsonc.ReadFile(expectedFile, out _), out _)!;
            Check(window.RestoredSession && window.PreviousSession?.Interrupted == true && window.Tabs.Count == 2,
                "a fresh process restores the actual abruptly terminated session");
            if (!window.RestoredSession || window.Tabs.Count != 2)
            {
                Log($"=== selftest ({Mode}) result: FAILED ===");
                return;
            }
            Check(window.Catalog.DefaultProfile?.StartingDirectory?.EndsWith("Shubbak", StringComparison.Ordinal) == true,
                "the restarted profile deliberately defaults to an unrelated project");
            foreach (var saved in expected)
            {
                var tab = window.Tabs.Single(t => t.NamingId == saved.NamingId);
                Check(tab.WorkingDirectory == saved.WorkingDirectory && tab.Project == saved.Project && tab.Detail.Contains(saved.Project!, StringComparison.Ordinal),
                    $"{saved.Project}: the subtitle is correct before any live integration report or tab interaction");
                Check(tab.Agent.SessionTitle == saved.SessionTitle && tab.SessionId == saved.SessionId && tab.Id != saved.Id,
                    $"{saved.Project}: saved session identity belongs to the correct newly created terminal");
            }
            Check(window.Tabs.Select(t => t.Badge?.Text).SequenceEqual(new[] { "1", "2" }) && window.CurrentKeyMode is null,
                "hints are regenerated for new runtime IDs; no transient selection mode is restored");
            await Task.Delay(150);
            Check(FindAll<System.Windows.Controls.TextBlock>(window.Strip).Where(t => t.Name == "TabDetail")
                    .All(t => t.DataContext is TerminalTab tab && t.Text.Contains(tab.Project, StringComparison.Ordinal) && !t.Text.Contains("Shubbak", StringComparison.Ordinal)),
                "the real restored tab templates show their own projects on the first run after the crash");
        }
        Log($"=== selftest ({Mode}) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    private static async Task RunDetailsAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (details) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var root = Path.Combine(AppPaths.StateRoot, "details-fixture");
        var alpha = Repository("alpha", "alpha-branch");
        var beta = Repository("beta", "beta-branch");
        var unrelated = Directory.CreateDirectory(Path.Combine(root, "Shubbak")).FullName;
        var profile = first.Profile with { StartingDirectory = unrelated, CommandLine = "powershell.exe -NoProfile -NoLogo" };
        var saved = new SavedTab
        {
            ProfileId = profile.Id, WorkingDirectory = alpha, SessionTitle = "Restored alpha task",
            SessionId = "details-alpha", Harness = "selftest", AgentRunning = true,
            ResumeCommand = "Write-Host details-resume-ready",
        };
        var tab = window.OpenSavedTab(saved, activate: false)!;
        try
        {
            Check(tab.WorkingDirectory == alpha && tab.Label == "Restored alpha task" && tab.Detail.Contains("alpha", StringComparison.Ordinal),
                "restoration has the saved title and its own project before live reports or activation");
            await TransferUntilAsync(() => tab.IsRunning);
            tab.SendText("[Console]::Write(([string][char]27) + ']9;9;' + $pwd.Path + ([char]7))\r");
            Check(await TransferUntilAsync(() => tab.AnnouncesDirectory), "the restored shell starts and announces its directory through ConPTY");
            tab.ApplyReport(Report(1, alpha));
            tab.ApplyProbedWorkingDirectory(unrelated, "a shell below an agent tool", aboutRootShell: false);
            Check(tab.WorkingDirectory == alpha && !tab.Detail.Contains("Shubbak", StringComparison.Ordinal),
                "a nested-shell probe cannot replace an integration-owned session directory");
            tab.ApplyReport(Report(2, alpha));
            tab.RefreshGit(DateTimeOffset.Now.AddSeconds(3), true, null);
            Check(tab.Branch == "alpha-branch", "the first project's branch is discovered");
            tab.ApplyReport(Report(3, beta) with { MetadataOnly = true });
            Check(tab.WorkingDirectory == beta && !tab.Detail.Contains("alpha-branch", StringComparison.Ordinal) && tab.GitRoot != alpha,
                "moving the session immediately removes the old project's branch and repository");
            tab.RefreshGit(DateTimeOffset.Now.AddSeconds(6), true, null);
            Check(tab.Branch == "beta-branch", "the moved session acquires its own branch");
            tab.RefreshGit(DateTimeOffset.Now.AddSeconds(9), false, null);
            tab.RefreshGit(DateTimeOffset.Now.AddSeconds(12), true, null);
            Check(tab.Branch == "beta-branch", "turning Git decoration off and back on restores an unchanged HEAD");
            window.SaveSession(force: true);
            var captured = SessionSnapshot.Load(AppPaths.SessionFile, out _)!.Tabs.Single(t => t.NamingId == tab.NamingId);
            Check(captured.WorkingDirectory == beta && captured.Project == "beta" && captured.SessionTitle == "Restored alpha task",
                "autosave keeps session title and the authoritative directory together");
            window.ActiveTab = first;
            await Task.Delay(150);
            Check(FindAll<System.Windows.Controls.TextBlock>(window.Strip).Any(t => t.Name == "TabDetail" && ReferenceEquals(t.DataContext, tab) && t.Text.Contains("beta", StringComparison.Ordinal)),
                "a background tab's real detail binding updates without visiting it");
            tab.ApplyReport(Report(4, null) with { Release = true, State = AgentState.Exited });
            Check(tab.WorkingDirectory == alpha, "releasing the integration returns to the shell's own directory");
            window.CloseTab(tab);
            var reopened = window.ReopenClosedTab(captured)!;
            Check(reopened.WorkingDirectory == beta && reopened.Detail.Contains("beta", StringComparison.Ordinal),
                "reopening uses the saved session directory, independent of the profile default");
            window.CloseTab(reopened);
        }
        finally { if (window.Tabs.Contains(tab)) window.CloseTab(tab); }
        Log($"=== selftest (details) result: {(pass ? "ALL PASS" : "FAILED")} ===");

        IntegrationReport Report(long seq, string? cwd) => new(tab.Id, "details-fixture", seq, AgentState.Idle,
            "selftest", null, null, "details-alpha", "Write-Host details-resume-ready", false,
            SessionTitle: "Restored alpha task", SessionKnown: true, WorkingDirectory: cwd);
        string Repository(string name, string branch)
        {
            var path = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            Directory.CreateDirectory(Path.Combine(path, ".git"));
            File.WriteAllText(Path.Combine(path, ".git", "HEAD"), $"ref: refs/heads/{branch}\n");
            return path;
        }
    }
}
