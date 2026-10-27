using System.IO;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Integrations;
using OverShell.Core.Settings;
using OverShell.Core.Tabs;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static async Task RunLabelsAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (labels) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var settings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;
        var root = Path.Combine(AppPaths.StateRoot, "label-probe");
        var alpha = Directory.CreateDirectory(Path.Combine(root, "alpha")).FullName;
        var beta = Directory.CreateDirectory(Path.Combine(root, "beta")).FullName;
        var profile = first.Profile with { StartingDirectory = alpha, CommandLine = "powershell.exe -NoProfile -NoLogo -NoExit -Command \"Write-Host 'opencode label probe'\"" };
        var one = window.AddTab(profile, activate: true);
        var two = window.AddTab(profile, activate: false);
        var extra = new List<TerminalTab>();
        try
        {
            await TransferUntilAsync(() => one.IsRunning && two.IsRunning && one.TerminalHwnd != 0 && two.TerminalHwnd != 0);
            await Task.Delay(700);
            one.ApplyReport(LabelReport(one, 1, "a", "Shopping list bug fixes"));
            two.ApplyReport(LabelReport(two, 1, "b", "Animation performance"));
            Check(one.Label == "Shopping list bug fixes" && two.Label == "Animation performance", "sessions in one directory have descriptive primary labels");
            Check(one.Detail.Contains("alpha", StringComparison.Ordinal) && one.Detail.Contains("idle", StringComparison.Ordinal), "project and state remain in the secondary line");
            Check(window.Title == one.Label + " — OverShell", "the main window caption follows the visible label");
            await Task.Delay(100);
            Check(FindAll<System.Windows.Controls.TextBlock>(window.Strip).Any(t => t.Name == "TabLabel" && t.Text == one.Label), "the real tab-strip binding shows the session title");

            one.ApplyReport(LabelReport(one, 2, "a", "Renamed while working") with { State = AgentState.Working });
            one.ApplyReport(LabelReport(one, 3, "a", "Immediate rename") with { MetadataOnly = true });
            Check(one.Label == "Immediate rename" && one.State == AgentState.Working, "a metadata-only rename updates immediately without completing the turn");
            one.ApplyReport(LabelReport(one, 2, "old-session", "Stale") with { ResumeCommand = "wrong", WorkingDirectory = beta, State = AgentState.Blocked, RequestId = "old-request" });
            Check(one.Label == "Immediate rename" && one.SessionId == "a" && one.ResumeCommand != "wrong" && one.WorkingDirectory == alpha && one.OpenRequest is null, "stale reports cannot change labels, session identity, directory, replies or resume targets");

            window.ApplySetting(SettingsCatalog.Find("tabs.labelMode")!, "\"project\"");
            Check(await TransferUntilAsync(() => one.Label == "alpha — Immediate rename"), "project-first labels reload live through the settings file");
            window.ApplySetting(SettingsCatalog.Find("tabs.labelMode")!, "\"harness\"");
            Check(await TransferUntilAsync(() => one.Label.EndsWith("OpenCode", StringComparison.Ordinal) && two.Label.EndsWith("OpenCode", StringComparison.Ordinal) && one.Label != two.Label), "harness mode still distinguishes duplicate agent names");
            window.ApplySetting(SettingsCatalog.Find("tabs.labelMode")!, "\"session\"");
            await TransferUntilAsync(() => window.CurrentSettings.Tabs.EffectiveLabelMode == TabLabelMode.Session);

            one.ApplyReport(LabelReport(one, 4, "a", "Shared task"));
            two.ApplyReport(LabelReport(two, 2, "b", "Shared task"));
            var before = one.Label;
            Check(one.Label != two.Label && one.Label.StartsWith('[') && two.Label.StartsWith('['), "identical tasks get distinct identifiers before text can be truncated");
            window.Tabs.Move(window.Tabs.IndexOf(two), window.Tabs.IndexOf(one));
            Check(one.Label == before, "identifiers do not change when tabs are reordered");
            var other = window.AddTab(profile with { StartingDirectory = beta }, activate: false);
            extra.Add(other);
            await TransferUntilAsync(() => other.IsRunning);
            other.ApplyReport(LabelReport(other, 1, "c", "Shared task"));
            Check(other.Label == "beta — Shared task", "a different project supplies a meaningful duplicate qualifier");

            window.RenameTab(one, "My own name");
            Check(one.Label == "My own name", "manual names override automatic labels");
            var tearOff = window.Detach(one);
            await Task.Delay(150);
            one.ApplyReport(LabelReport(one, 5, "a", "Another task name"));
            Check(one.Label == "My own name" && tearOff?.Title == "My own name — OverShell", "custom names remain stable in a detached window during session changes");
            window.Attach(one);
            window.RenameTab(one, null);
            Check(one.Label == "Another task name", "clearing a custom name restores the current automatic name");

            var oldCache = PersistedState.Load(AppPaths.StateFile);
            oldCache.SetLabel(profile.Id, alpha, "legacy folder-wide name");
            var fresh = window.AddTab(profile, activate: false);
            extra.Add(fresh);
            Check(fresh.UserLabel is null && fresh.Label != "legacy folder-wide name", "new tabs never inherit another tab's folder-wide custom name");

            two.ApplyReport(LabelReport(two, 3, "terminal-title", null));
            two.SendText("[Console]::Write(([string][char]27) + ']0;OC | alpha | Native title | with pipe' + ([char]7))\r");
            Check(await TransferUntilAsync(() => two.Label == "Native title | with pipe"), "terminal title announcements provide names without title metadata");
            two.ApplyReport(LabelReport(two, 4, "untitled", ""));
            Check(two.BaseLabel == "alpha" && two.SessionTitle is null, "an explicitly untitled new session does not reuse an old terminal title");
            two.ApplyReport(LabelReport(two, 5, "untitled", "") with { State = AgentState.Exited, Release = true });
            Check(two.BaseLabel == "alpha", "releasing the title bridge cannot revive an old session's terminal title");
            two.SendText("[Console]::Write(([string][char]27) + ']0;OC | alpha | Native title | with pipe' + ([char]7))\r");
            Check(await TransferUntilAsync(() => two.Label == "Native title | with pipe"), "a fresh repeated terminal title can take over after the bridge unloads");

            one.SendText("[Console]::Write(([string][char]27) + ']9;9;' + $pwd.Path + ([char]7))\r");
            await TransferUntilAsync(() => one.AnnouncesDirectory);
            one.ApplyReport(LabelReport(one, 6, "a", "Moved task") with { WorkingDirectory = beta, MetadataOnly = true });
            var output = one.OutputVersion;
            one.SendText("Write-Host ordinary-output-after-session-move\r");
            await TransferUntilAsync(() => one.OutputVersion > output);
            Check(one.WorkingDirectory == beta && one.Detail.Contains("beta", StringComparison.Ordinal), "ordinary output cannot replay an old directory over a session move");
            one.SendText($"[Console]::Write(([string][char]27) + ']7;{new Uri(alpha).AbsoluteUri}' + ([char]7))\r");
            Check(await TransferUntilAsync(() => one.WorkingDirectory == alpha), "a new OSC 7 directory overrides an older OSC 9;9 announcement");

            window.RenameTab(one, "First custom");
            window.RenameTab(two, "Second custom");
            var saved = SessionSnapshot.Load(AppPaths.SessionFile, out _)!;
            Check(saved.Tabs.Single(t => t.NamingId == one.NamingId).Label == "First custom" && saved.Tabs.Single(t => t.NamingId == two.NamingId).Label == "Second custom", "different custom names in the same folder are saved independently");
            var id = one.NamingId;
            window.CloseTab(one);
            var entry = window.RecentlyClosed.Last();
            Check(window.BuildHistoryItems(Core.Search.PaletteQuery.Parse("Moved task")).Count > 0, "session history can be found by the recorded session name even with a custom label");
            var reopened = window.ReopenClosedTab(entry)!;
            extra.Add(reopened);
            Check(reopened.NamingId == id && reopened.Label == "First custom" && two.Label == "Second custom", "reopening a tab restores its own name and stable identity");
            var clone = window.OpenSavedTab(entry, activate: false)!;
            extra.Add(clone);
            Check(clone.NamingId != reopened.NamingId, "opening the same history entry twice creates distinct identities");
        }
        finally
        {
            foreach (var tab in extra.Append(one).Append(two).ToArray()) if (window.Tabs.Contains(tab)) window.CloseTab(tab);
            if (settings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllText(AppPaths.SettingsFile, settings);
        }
        Log($"=== selftest (labels) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    private static IntegrationReport LabelReport(TerminalTab tab, long seq, string session, string? title) =>
        new(tab.Id, "label-selftest", seq, AgentState.Idle, "opencode", null, null, session, "Write-Host label-restored", false,
            SessionTitle: title, SessionKnown: true);

    private static async Task RunLabelsSaveAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (labels1) start ===");
        var tabs = new[] { first, window.AddTab(first.Profile, false), window.AddTab(first.Profile, false), window.AddTab(first.Profile, false) };
        await TransferUntilAsync(() => tabs.All(t => t.IsRunning));
        for (var i = 0; i < tabs.Length; i++) tabs[i].ApplyReport(LabelReport(tabs[i], 1, "label-session-" + i, "Restart task"));
        window.RenameTab(tabs[0], "left");
        window.RenameTab(tabs[1], "right");
        window.SaveSession(force: true);
        File.WriteAllText(Path.Combine(AppPaths.StateRoot, "label-expectations.json"), Jsonc.Serialize(tabs.Select(t => new { t.NamingId, t.Label, t.UserLabel }).ToArray()));
        Log($"  {(tabs.Select(t => t.Label).Distinct().Count() == 4 ? "PASS" : "FAIL")}  four tabs have independent names before restart");
        Log("=== selftest (labels1) result: closing ===");
        window.Close();
    }

    private static async Task RunLabelsRestoreAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (labels2) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var expected = Jsonc.ReadFile(Path.Combine(AppPaths.StateRoot, "label-expectations.json"), out _)!.AsArray();
        Check(window.RestoredSession && window.Tabs.Count == 4, "the preceding run's four tabs were restored");
        for (var i = 0; i < Math.Min(expected.Count, window.Tabs.Count); i++)
        {
            var tab = window.Tabs[i];
            Check(tab.NamingId == expected[i]!["namingId"]!.GetValue<string>() && tab.UserLabel == expected[i]!["userLabel"]?.GetValue<string>(), $"tab {i + 1} kept its persistent identity and its own custom name");
            tab.ApplyReport(LabelReport(tab, 1, "label-session-" + i, "Restart task"));
        }
        Check(window.Tabs.Select(t => t.Label).SequenceEqual(expected.Select(t => t!["label"]!.GetValue<string>())), "automatic duplicate qualifiers are identical after a real restart");
        var added = window.AddTab(first.Profile, false);
        await TransferUntilAsync(() => added.IsRunning);
        Check(added.UserLabel is null, "a fresh tab in the restored directory inherits no custom name");
        window.CloseTab(added);
        Log($"=== selftest (labels2) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }
}
