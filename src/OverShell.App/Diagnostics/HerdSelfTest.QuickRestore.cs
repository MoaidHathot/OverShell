using System.IO;
using System.Text.Json.Nodes;
using OverShell.Core;
using OverShell.Core.Extensibility;
using OverShell.Core.Input;
using OverShell.Core.Settings;
using OverShell.Core.Tabs;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private static bool QuickAccessMode => Mode is "quicknav" or "aliases" or "floats" or "scratch" or "quickrestore1" or "quickrestore2";

    private static void SeedQuickRestore()
    {
        if (Mode == "quickrestore1")
        {
            SeedPreviewBenchmark();
            var settings = Jsonc.ReadFile(AppPaths.SettingsFile, out _)!.AsObject();
            settings["session"]!["restore"] = true;
            File.WriteAllText(AppPaths.SettingsFile, settings.ToJsonString());
        }
        Environment.SetEnvironmentVariable("OVERSHELL_WT_SETTINGS", Path.Combine(AppPaths.StateRoot, "preview-terminal.json"));
    }

    private static async Task RunQuickRestoreAsync(MainWindow window, TerminalTab first)
    {
        Log($"=== selftest ({Mode}) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var evidence = Path.Combine(AppPaths.StateRoot, "quick-restore-expected.json");
        if (Mode == "quickrestore1")
        {
            first.UserLabel = "qa-persistent";
            window.Addresses.Assign(first.Id, "qa-session");
            window.Shell.Keys.Assign(first.Id, new Keybinding("ctrl+alt+f23", "tab.activate"));
            var ephemeral = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, Scratch: true, Floating: true, Label: "qa-ephemeral", Alias: "qa-ephemeral"))!;
            var kept = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, Scratch: true, Label: "qa-kept", Alias: "qa-kept", Activate: false))!;
            window.SetTabLifetime(kept.Id, TabLifetime.Standard);
            var policy = window.OpenTabFor(new TabRequest(Profile: first.Profile.Id, Scratch: true, Restore: true, History: false, Label: "qa-policy", Activate: false))!;
            Check(await TransferUntilAsync(() => window.Tabs.All(t => t.IsRunning)), "all lifecycle fixtures have real running processes");
            window.ActiveTab = first;
            window.Commands.TryExecute("session.save");
            var snapshot = SessionSnapshot.Load(AppPaths.SessionFile, out _)!;
            Check(snapshot.Tabs.Count == 3 && snapshot.Tabs.All(t => t.Id != ephemeral.Id), "only restorable fixtures enter the session");
            Check(snapshot.Tabs.Any(t => t.Label == "qa-kept") && snapshot.Tabs.Single(t => t.Label == "qa-policy").Lifetime is { Scratch: true, Restore: true, History: false }, "promotion and explicit retention policy are captured");
            File.WriteAllText(evidence, Jsonc.Serialize(new { first.Id, first.NamingId }));
        }
        else
        {
            var expected = JsonNode.Parse(File.ReadAllText(evidence))!;
            Check(window.RestoredSession && window.Tabs.Count == 3, "a new application process restores the retained session");
            Check(window.Tabs.All(t => t.UserLabel != "qa-ephemeral"), "transient scratch tabs stay absent after restart");
            Check(window.Tabs.All(t => t.Alias is null && t.ShortcutHint.Length == 0) && window.Shell.Keys.Runtime.Count == 0,
                "all runtime aliases and shortcut registrations expire across restart");
            Check(window.Addresses.Resolve(new TabSelector(Alias: "qa-session")).TabId is null, "the alias registry starts empty");
            var persistent = window.Tabs.Single(t => t.UserLabel == "qa-persistent");
            Check(persistent.Id != expected["id"]?.GetValue<string>() && persistent.NamingId == expected["namingId"]?.GetValue<string>(), "runtime IDs renew while persistent naming identity survives");
            Check(window.Tabs.Single(t => t.UserLabel == "qa-kept").Lifetime == TabLifetime.Standard, "a kept scratch tab restores as a normal tab");
            Check(window.Tabs.Single(t => t.UserLabel == "qa-policy").Lifetime is { Scratch: true, Restore: true, History: false }, "explicit retention policy survives restoration");
            Check(window.ActiveTab == persistent && window.FloatingWindows.Count == 0 && await TransferUntilAsync(() => window.Tabs.All(t => t.IsRunning)),
                "the original work is selected, fresh processes run, and temporary floating hosts are absent");
        }
        Log($"=== selftest ({Mode}) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }
}
