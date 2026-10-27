using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OverShell.App.Chrome;
using OverShell.Core;
using OverShell.Core.Extensibility;
using OverShell.Core.Settings;

namespace OverShell.App.Diagnostics;

internal static partial class HerdSelfTest
{
    private const string ProfileOne = "11111111-aaaa-4444-8888-111111111111";
    private const string ProfileTwo = "22222222-bbbb-4444-8888-222222222222";
    private const string ProfileThree = "33333333-cccc-4444-8888-333333333333";
    private static string? _profilesSettingsBefore;
    private static string? _profilesEnvironmentBefore;
    private static string ProfileFixture => Path.Combine(AppPaths.StateRoot, "profiles", "imported.json");

    private static string ProfileFixtureJson(string secondName = "Profile Cmd") => $$"""
        {
          "defaultProfile": "Profile PowerShell",
          "profiles": { "list": [
            { "guid": "{{ProfileOne}}", "name": "Profile PowerShell", "commandline": "powershell.exe -NoLogo -NoProfile", "startingDirectory": "%USERPROFILE%" },
            { "guid": "{{ProfileTwo}}", "name": "{{secondName}}", "commandline": "cmd.exe /Q", "startingDirectory": "%USERPROFILE%" },
            { "guid": "{{ProfileThree}}", "name": "@remote #dev", "commandline": "cmd.exe /Q" },
            { "guid": "44444444-dddd-4444-8888-444444444444", "name": "Duplicate", "commandline": "cmd.exe /Q" },
            { "guid": "55555555-eeee-4444-8888-555555555555", "name": "Duplicate", "commandline": "cmd.exe /Q" }
          ] }
        }
        """;

    private static void SeedProfiles()
    {
        _profilesSettingsBefore = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;
        _profilesEnvironmentBefore = Environment.GetEnvironmentVariable("OVERSHELL_WT_SETTINGS");
        Environment.SetEnvironmentVariable("OVERSHELL_WT_SETTINGS", null);
        Directory.CreateDirectory(Path.GetDirectoryName(ProfileFixture)!);
        File.WriteAllText(ProfileFixture, ProfileFixtureJson());
        File.WriteAllText(AppPaths.SettingsFile, Jsonc.Serialize(new
        {
            profiles = new { settingsPath = ProfileFixture, defaultProfile = "Profile Cmd" },
            session = new { restore = false, confirmCloseWithAgents = false },
            protocol = new { register = false },
        }));
    }

    private static TextBox ProfileInput(PaletteWindow palette) => (TextBox)palette.FindName("Input");
    private static List<PaletteItem> ProfileRows(PaletteWindow palette) => ((ListBox)palette.FindName("List")).Items.Cast<PaletteItem>().ToList();

    // Routed keyboard events exercise the real picker handlers, on a real focused window,
    // without sending input to the user's desktop.
    private static void ProfileKey(PaletteWindow palette, Key key) => ProfileInput(palette).RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(palette), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static async Task RunProfilesAsync(MainWindow window, TerminalTab first)
    {
        Log("=== selftest (profiles) start ===");
        var pass = true;
        void Check(bool ok, string what) { pass &= ok; Log($"  {(ok ? "PASS" : "FAIL")}  {what}"); }
        var keysBefore = File.Exists(AppPaths.KeybindingsFile) ? File.ReadAllText(AppPaths.KeybindingsFile) : null;
        try
        {
            Check(first.Profile.Id == ProfileTwo && first.IsRunning, "startup uses OverShell's configured default rather than the imported default");
            Check(window.Catalog.SettingsPath == ProfileFixture, "startup imports the configured settings file");
            Check(window.DispatchChord(Key.T, ModifierKeys.Control | ModifierKeys.Shift) && window.ActiveTab?.Profile.Id == ProfileTwo,
                "Ctrl+Shift+T launches the configured default");
            await TransferUntilAsync(() => window.ActiveTab?.TerminalHwnd != 0);

            Check(window.DispatchChord(Key.N, ModifierKeys.Control | ModifierKeys.Shift), "Ctrl+Shift+N resolves to the profile picker");
            await Task.Delay(150);
            var palette = window.Palette!;
            Check(ProfileInput(palette).IsKeyboardFocused, "the picker takes keyboard focus from the native terminal");
            Check(ProfileRows(palette).Any(r => r.Title == "Profile Cmd" && r.Hint == "default"), "the effective default is marked");
            Check(((TextBlock)palette.FindName("TxtContext")).Text.Contains(ProfileFixture, StringComparison.Ordinal), "the resolved source path is visible in the picker");
            var list = (ListBox)palette.FindName("List");
            ProfileKey(palette, Key.Down);
            Check(list.SelectedIndex == 1, "Down moves through profiles");
            ProfileKey(palette, Key.Up);
            Check(list.SelectedIndex == 0, "Up moves through profiles");
            ProfileInput(palette).Text = "@remote #dev";
            Check(ProfileRows(palette).Count == 1 && ProfileRows(palette)[0].Title == "@remote #dev", "profile names containing @ and # are searched literally");
            ProfileInput(palette).Text = "Duplicate";
            var duplicates = ProfileRows(palette).Where(r => r.Title.EndsWith(" Duplicate", StringComparison.Ordinal)).ToArray();
            Log("  duplicate search: " + string.Join(" | ", ProfileRows(palette).Select(r => r.Title)));
            Check(duplicates.Length == 2 && duplicates.Select(r => r.Title).Distinct().Count() == 2, "duplicate names show distinct profile identifiers");
            ProfileInput(palette).Text = "not-a-real-profile-98765";
            var count = window.Tabs.Count;
            ProfileKey(palette, Key.Return);
            Check(window.Tabs.Count == count && palette.IsVisible && ProfileRows(palette).Count == 0, "Enter with no matches opens nothing");
            ProfileInput(palette).Text = ProfileOne;
            Check(ProfileRows(palette).Count == 1, "profiles can be found by GUID");
            ProfileKey(palette, Key.Return);
            Check(await TransferUntilAsync(() => window.Tabs.Count == count + 1 && window.ActiveTab?.Profile.Id == ProfileOne), "Enter opens the selected profile exactly once");
            Check(await TransferUntilAsync(() => ShortcutRouter.FocusedWindow() == window.ActiveTab?.TerminalHwnd), "the newly opened native terminal receives focus");

            window.Commands.TryExecute("tab.newWithProfile");
            await Task.Delay(100);
            count = window.Tabs.Count;
            ProfileKey(window.Palette!, Key.Escape);
            Check(window.Palette is null && window.Tabs.Count == count, "Escape cancels the profile picker");
            Check(await TransferUntilAsync(() => ShortcutRouter.FocusedWindow() == window.ActiveTab?.TerminalHwnd), "cancelling restores native terminal focus");

            var command = MainWindow.ProfileCommandId(window.Catalog.FindById(ProfileThree)!);
            Check(window.Commands.Find(command)?.Title.Contains("@remote #dev", StringComparison.Ordinal) == true, "each profile has a searchable, bindable command");
            File.WriteAllText(AppPaths.KeybindingsFile, Jsonc.Serialize(new[] { new { keys = "ctrl+alt+9", command } }));
            Check(await TransferUntilAsync(() => window.HintFor(command) == "Ctrl+Alt+9"), "direct-profile keybindings reload from the user's file");
            Check(window.DispatchChord(Key.D9, ModifierKeys.Control | ModifierKeys.Alt) && window.ActiveTab?.Profile.Id == ProfileThree, "a custom shortcut launches its exact profile");

            foreach (var view in new[] { "herd", "dashboard", "zen" })
            {
                window.Commands.TryExecute("view." + view);
                window.Commands.TryExecute("tab.newWithProfile");
                await Task.Delay(100);
                ProfileInput(window.Palette!).Text = ProfileTwo;
                count = window.Tabs.Count;
                ProfileKey(window.Palette!, Key.Return);
                Check(await TransferUntilAsync(() => window.Palette is null && window.Tabs.Count == count + 1 && window.ActiveTab?.Profile.Id == ProfileTwo && window.ViewId != "dashboard"), $"profile selection opens a terminal from {view} view");
            }

            var tearOff = window.Detach(first)!;
            tearOff.Activate();
            await Task.Delay(150);
            window.Commands.TryExecute("tab.newWithProfile");
            await Task.Delay(150);
            Check(ReferenceEquals(window.Palette?.Owner, tearOff), "a detached terminal owns its profile picker");
            ProfileKey(window.Palette!, Key.Escape);
            Check(await TransferUntilAsync(() => ShortcutRouter.FocusedWindow() == first.TerminalHwnd), "Escape returns focus to the detached terminal");
            window.Commands.TryExecute("palette.commands");
            await Task.Delay(100);
            Check(ReferenceEquals(window.Palette?.Owner, tearOff), "the command palette also belongs to the detached terminal");
            ProfileInput(window.Palette!).Text = ">tab.newWithProfile";
            ProfileKey(window.Palette!, Key.Return);
            Check(await TransferUntilAsync(() => window.Palette?.Title == "New tab with profile — OverShell"), "the command palette opens the profile picker with the keyboard");
            Check(ReferenceEquals(window.Palette?.Owner, tearOff), "the picker retains its detached owner through command-palette navigation");
            ProfileInput(window.Palette!).Text = ProfileTwo;
            count = window.Tabs.Count;
            ProfileKey(window.Palette!, Key.Return);
            Check(await TransferUntilAsync(() => window.Tabs.Count == count + 1 && window.IsActive && window.ActiveTab?.Profile.Id == ProfileTwo && ShortcutRouter.FocusedWindow() == window.ActiveTab.TerminalHwnd), "selecting from a detached picker activates and focuses the new main-window tab");
            Log($"  selected tab focus: count={window.Tabs.Count} expected={count + 1} mainActive={window.IsActive} tearOffActive={tearOff.IsActive} id={window.ActiveTab?.Profile.Id} visible={window.ActiveTab?.View.IsVisible} focused=0x{ShortcutRouter.FocusedWindow():X} terminal=0x{window.ActiveTab?.TerminalHwnd:X} logical={Keyboard.FocusedElement?.GetType().Name}");
            window.Attach(first);

            window.Commands.TryExecute("settings.set.profiles.defaultProfile");
            await Task.Delay(100);
            Check(ProfileRows(window.Palette!).Any(r => r.Title == "Profile Cmd" && r.Hint == "current") &&
                  ProfileRows(window.Palette!).Any(r => r.Title == "follow Windows Terminal"), "the default-profile setting offers loaded profiles and the imported-default choice");
            ProfileKey(window.Palette!, Key.Escape);

            // Regression: Closed fires before the selected action; it must not complete these
            // extension tasks as cancelled when Enter accepted a value.
            PaletteWindow ExtensionPalette() => Application.Current.Windows.OfType<PaletteWindow>().Single(w => w.IsVisible);
            var pick = new PickItem("Choose this");
            var picked = window.Shell.Ui.PickAsync("\uE756", "No choices", [pick]);
            await Task.Delay(100);
            ProfileKey(ExtensionPalette(), Key.Return);
            Check(ReferenceEquals(await picked, pick), "extension pickers return the accepted item after closing");
            var prompted = window.Shell.Ui.PromptAsync("Prompt", "accepted text");
            await Task.Delay(100);
            ProfileKey(ExtensionPalette(), Key.Return);
            Check(await prompted == "accepted text", "extension prompts return accepted text after closing");
            var asked = window.Shell.Ui.AskAsync("Question", ["First", "Second"]);
            await Task.Delay(100);
            ProfileKey(ExtensionPalette(), Key.Down);
            ProfileKey(ExtensionPalette(), Key.Return);
            Check(await asked == 1, "extension questions return the selected answer");
            picked = window.Shell.Ui.PickAsync("\uE756", "No choices", [pick]);
            await Task.Delay(100);
            ProfileKey(ExtensionPalette(), Key.Escape);
            Check(await picked is null, "Escape still cancels an extension picker");

            await RunProfileReloadsAsync(window, first, Check);
        }
        finally
        {
            window.Palette?.Close();
            foreach (var tab in window.Tabs.Where(t => !ReferenceEquals(t, first)).ToArray()) window.CloseTab(tab);
            if (keysBefore is null) File.Delete(AppPaths.KeybindingsFile); else File.WriteAllText(AppPaths.KeybindingsFile, keysBefore);
            if (_profilesSettingsBefore is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllText(AppPaths.SettingsFile, _profilesSettingsBefore);
            Environment.SetEnvironmentVariable("OVERSHELL_WT_SETTINGS", _profilesEnvironmentBefore);
        }
        Log($"=== selftest (profiles) result: {(pass ? "ALL PASS" : "FAILED")} ===");
    }

    private static async Task RunProfileReloadsAsync(MainWindow window, TerminalTab first, Action<bool, string> check)
    {
        var originalProfile = first.Profile;
        var originalHwnd = first.TerminalHwnd;
        var secondCommand = MainWindow.ProfileCommandId(window.Catalog.FindById(ProfileTwo)!);
        var thirdCommand = MainWindow.ProfileCommandId(window.Catalog.FindById(ProfileThree)!);

        window.Commands.TryExecute("settings.set.profiles.defaultProfile");
        await Task.Delay(100);
        ProfileInput(window.Palette!).Text = "Profile PowerShell";
        ProfileKey(window.Palette!, Key.Return);
        check(await TransferUntilAsync(() => window.Catalog.DefaultProfile?.Id == ProfileOne), "choosing a default in the settings picker applies live");
        window.Commands.TryExecute("tab.new");
        check(window.ActiveTab?.Profile.Id == ProfileOne, "new-tab commands use the live default");
        window.ApplySetting(SettingsCatalog.Find("profiles.defaultProfile")!, "null");
        await TransferUntilAsync(() => window.CurrentSettings.Profiles.DefaultProfile is null);
        await window.ProfileReloadTask;
        check(window.Catalog.DefaultProfile?.Id == ProfileOne, "clearing the override follows the imported name-based default");

        var other = Path.Combine(Path.GetDirectoryName(ProfileFixture)!, "other.json");
        File.WriteAllText(other, ProfileFixtureJson("Other Cmd"));
        window.ApplySetting(SettingsCatalog.Find("profiles.settingsPath")!, JsoncEdit.ToJson(other));
        check(await TransferUntilAsync(() => window.Catalog.SettingsPath == other), "changing settingsPath swaps the imported source live");
        check(window.Commands.Find(secondCommand)?.Title == "New tab: Other Cmd", "profile commands are re-registered from the new source");
        window.Commands.TryExecute(secondCommand);
        check(window.ActiveTab?.Profile.Name == "Other Cmd", "direct commands launch the reloaded profile definition");
        check(ReferenceEquals(first.Profile, originalProfile) && first.TerminalHwnd == originalHwnd && first.IsRunning, "existing tabs retain their profile and live native session across a source change");

        window.Commands.TryExecute("tab.newWithProfile");
        await Task.Delay(100);
        ProfileInput(window.Palette!).Text = ProfileTwo;
        File.WriteAllText(other + ".tmp", ProfileFixtureJson("Renamed Cmd"));
        File.Move(other + ".tmp", other, overwrite: true);
        check(await TransferUntilAsync(() => window.Catalog.FindById(ProfileTwo)?.Name == "Renamed Cmd"), "atomic replacement of the imported file reloads automatically");
        check(window.Palette is { IsVisible: true } && ProfileRows(window.Palette).Single().Title == "Renamed Cmd", "an open profile picker refreshes without losing keyboard focus");
        ProfileKey(window.Palette!, Key.Escape);

        var good = window.Catalog;
        File.WriteAllText(other, "{ temporarily invalid");
        check(await TransferUntilAsync(() => window.ProfileReloadError is not null) && ReferenceEquals(window.Catalog, good), "a malformed imported file retains the last good catalog with a diagnostic");
        File.WriteAllText(other, ProfileFixtureJson("Recovered Cmd"));
        check(await TransferUntilAsync(() => window.ProfileReloadError is null && window.Catalog.FindById(ProfileTwo)?.Name == "Recovered Cmd"), "fixing the imported file recovers automatically");

        good = window.Catalog;
        var settings = File.ReadAllText(AppPaths.SettingsFile);
        File.WriteAllText(AppPaths.SettingsFile, "{ temporarily invalid");
        check(await TransferUntilAsync(() => window.CurrentSettings.HasLoadError) && ReferenceEquals(window.Catalog, good), "malformed OverShell settings do not replace the loaded profile source");
        File.WriteAllText(AppPaths.SettingsFile, settings);
        await TransferUntilAsync(() => !window.CurrentSettings.HasLoadError);

        var missing = Path.Combine(Path.GetDirectoryName(ProfileFixture)!, "created-later", "settings.json");
        window.ApplySetting(SettingsCatalog.Find("profiles.settingsPath")!, JsoncEdit.ToJson(missing));
        check(await TransferUntilAsync(() => window.ProfileReloadError?.Contains(missing, StringComparison.Ordinal) == true) && ReferenceEquals(window.Catalog, good), "a missing explicit source reports its path and retains usable profiles");
        Directory.CreateDirectory(Path.GetDirectoryName(missing)!);
        File.WriteAllText(missing, ProfileFixtureJson());
        check(await TransferUntilAsync(() => window.Catalog.SettingsPath == missing && window.ProfileReloadError is null), "creating a missing source directory and file recovers through the watcher");

        window.Commands.TryExecute("tab.newWithProfile");
        await Task.Delay(100);
        ProfileInput(window.Palette!).Text = ProfileThree;
        var staleRow = ProfileRows(window.Palette!).Single();
        var removed = System.Text.Json.Nodes.JsonNode.Parse(ProfileFixtureJson())!;
        var profiles = removed["profiles"]!["list"]!.AsArray();
        profiles.RemoveAt(2);
        File.WriteAllText(missing, removed.ToJsonString());
        check(await TransferUntilAsync(() => window.Commands.Find(thirdCommand) is null), "removed profiles disappear from registered commands");
        check(window.Palette is not null && ProfileRows(window.Palette).Count == 0, "removed profiles disappear from the open picker");
        ProfileKey(window.Palette!, Key.Escape);
        var count = window.Tabs.Count;
        staleRow.Invoke();
        check(window.Tabs.Count == count, "a stale picker action cannot silently launch the default profile");
        check(window.DispatchChord(Key.D9, ModifierKeys.Control | ModifierKeys.Alt) && window.Tabs.Count == count,
            "a shortcut for a removed profile is handled with an error rather than sent to the shell");

        File.WriteAllText(missing, ProfileFixtureJson());
        await TransferUntilAsync(() => window.Catalog.FindById(ProfileThree) is not null);
        var previousLoad = window.ProfileReloadTask;
        window.ApplySetting(SettingsCatalog.Find("tabs.twoLine")!, "false");
        await TransferUntilAsync(() => !window.CurrentSettings.Tabs.TwoLine);
        check(ReferenceEquals(previousLoad, window.ProfileReloadTask), "unrelated settings changes do not rerun profile generators");
        window.Commands.TryExecute("settings.reload");
        var manual = window.ProfileReloadTask;
        await manual;
        check(!ReferenceEquals(previousLoad, manual) && window.ProfileReloadError is null, "Reload configuration also refreshes imported profiles");

        // Force an older load to finish last, proving edits cannot race back to old defaults.
        var loader = window.ProfileLoader;
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ProfileLoader = options =>
        {
            if (options.DefaultProfile == ProfileTwo)
            {
                entered.TrySetResult();
                if (!gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("profile load gate");
            }
            return loader(options);
        };
        try
        {
            window.ApplySetting(SettingsCatalog.Find("profiles.defaultProfile")!, JsoncEdit.ToJson(ProfileTwo));
            window.ReloadConfiguration(all: false, settings: true);
            var older = window.ProfileReloadTask;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.ApplySetting(SettingsCatalog.Find("profiles.defaultProfile")!, JsoncEdit.ToJson(ProfileOne));
            window.ReloadConfiguration(all: false, settings: true);
            await window.ProfileReloadTask;
            gate.Set();
            await older;
            check(window.Catalog.DefaultProfile?.Id == ProfileOne, "a slow older reload cannot overwrite a newer default selection");
        }
        finally { gate.Set(); window.ProfileLoader = loader; }

        count = window.Tabs.Count;
        check(window.OpenTabFor(new TabRequest(Profile: "missing-profile"), out var error) is null && error?.Contains("unavailable", StringComparison.Ordinal) == true && window.Tabs.Count == count,
            "extension launches reject missing profiles with a useful error");
        check(window.OpenTabFor(new TabRequest(Profile: "Duplicate"), out error) is null && error?.Contains("ambiguous", StringComparison.Ordinal) == true,
            "extension launches reject ambiguous names");
        var workspace = new Workspace { Name = "Profile resolution", Tabs = [new WorkspaceTab { Profile = "missing-profile" }, new WorkspaceTab { Profile = "{" + ProfileTwo.ToUpperInvariant() + "}" }] };
        check(window.OpenWorkspace(workspace) == 1 && window.Tabs.Count == count + 1 && window.ActiveTab?.Profile.Id == ProfileTwo, "workspace launches skip missing profiles and resolve braced GUIDs exactly");
        window.HandleArguments(["overshell://new?profile=missing-profile"]);
        check(window.Tabs.Count == count + 1, "protocol launches reject missing profiles");
        window.HandleArguments(["overshell://new?profile=" + Uri.EscapeDataString("{" + ProfileThree.ToUpperInvariant() + "}")]);
        check(window.Tabs.Count == count + 2 && window.ActiveTab?.Profile.Id == ProfileThree, "protocol launches resolve the same normalized profile IDs");

        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", window.Endpoint!.Token);
        using var refused = await http.PostAsync(window.Endpoint.BaseUrl + "/v1/tabs", new System.Net.Http.StringContent("{\"profile\":\"missing-profile\"}", System.Text.Encoding.UTF8, "application/json"));
        check(!refused.IsSuccessStatusCode && (await refused.Content.ReadAsStringAsync()).Contains("missing-profile", StringComparison.Ordinal) && window.Tabs.Count == count + 2,
            "the HTTP control API returns the profile error and opens no fallback tab");
        using var opened = await http.PostAsync(window.Endpoint.BaseUrl + "/v1/tabs", new System.Net.Http.StringContent(Jsonc.Serialize(new { profile = "{" + ProfileTwo.ToUpperInvariant() + "}" }), System.Text.Encoding.UTF8, "application/json"));
        check(opened.IsSuccessStatusCode && window.Tabs.Count == count + 3 && window.Tabs.Last().Profile.Id == ProfileTwo, "the HTTP control API opens an exact normalized profile ID");

        var duplicate = window.OpenTabFor(new TabRequest(Profile: "44444444-dddd-4444-8888-444444444444"))!;
        var savedWorkspace = window.SaveWorkspace("Duplicate profile round trip");
        check(WorkspaceCatalog.Load(AppPaths.WorkspacesDir).All.Single(w => w.Name == "Duplicate profile round trip").Tabs.Any(t => t.Profile == duplicate.Profile.Id),
            "saving a workspace preserves IDs for ambiguous profile names");
        File.Delete(savedWorkspace);
        window.SaveSession(force: true);
        var saved = SessionSnapshot.Load(AppPaths.SessionFile, out _)!.Tabs.First(t => t.ProfileId == ProfileTwo);
        check(saved.ProfileName == "Profile Cmd", "session snapshots retain the profile name for channel/machine changes");
        var restored = window.OpenSavedTab(new SavedTab { ProfileId = "other-machine-id", ProfileName = "Profile Cmd" }, activate: false)!;
        check(restored.Profile.Id == ProfileTwo, "restore resolves a saved unique name when its old profile ID is absent");
    }
}
