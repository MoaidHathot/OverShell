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
}
