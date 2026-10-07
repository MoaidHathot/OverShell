using System.IO;
using System.Text.Json.Nodes;
using OverShell.App.Chrome;
using OverShell.Core;
using OverShell.Core.Commands;
using OverShell.Core.Search;
using OverShell.Core.Settings;

namespace OverShell.App;

/// <summary>
/// Settings from the palette (§12.22): the two files opened in the editor (written from the
/// commented defaults when missing), a picker that changes one value at a time - the catalog's
/// knobs with their current values, then the values each takes - and the help entries that
/// point at the tutorial and the guide. The change lands in <c>settings.jsonc</c> through an
/// edit that keeps the rest of the file (comments included), and the file watcher applies it
/// the way a hand edit is applied: there is one path for a setting to take effect.
/// </summary>
public partial class MainWindow
{
    private const string DocsUrl = "https://github.com/MoaidHathot/OverShell/blob/main/docs/";

    private const string SettingsGlyph = "\uE713"; // gear
    private const string HelpGlyph = "\uE897";     // question in a circle

    /// <summary>What the last palette change asked for, named in the reload's status line so the cause and the effect read as one.</summary>
    private string? _settingChangeNote;

    private void RegisterSettingsAndHelpCommands(CommandRegistry c)
    {
        c.Register("settings.edit", "Open settings.jsonc", "Settings", () => OpenSettingsFile(AppPaths.SettingsFile, "settings.jsonc"),
            description: "In your editor; written from the commented defaults first when it does not exist. Saved changes apply live");
        c.Register("settings.editKeys", "Open keybindings.jsonc", "Settings", () => OpenSettingsFile(AppPaths.KeybindingsFile, "keybindings.jsonc"),
            description: "Key \u2192 command, Windows Terminal's shape; written from the defaults first when it does not exist");
        c.Register("settings.change", "Change a setting\u2026", "Settings", OpenSettingsPicker,
            description: "Pick a setting, then its value; the change is written into settings.jsonc and applied live");

        // One command per knob too, so typing "opacity" in the palette lands on it directly.
        foreach (var knob in SettingsCatalog.Knobs)
        {
            var captured = knob;
            c.Register($"settings.set.{knob.Path}", $"Setting: {knob.Title}", "Settings", () => OpenValuePicker(captured), description: knob.Description);
        }

        c.Register("help.tutorial", "Help: Tutorial", "Help", () => OpenLink(DocsUrl + "TUTORIAL.md"),
            description: "Ten minutes, hands on: tabs, an agent, herd mode, triage, sessions, the look (opens in the browser)");
        c.Register("help.guide", "Help: User guide", "Help", () => OpenLink(DocsUrl + "GUIDE.md"),
            description: "Everything, by topic: settings, keys, agents, integrations, sessions, the control API (opens in the browser)");
        c.Register("help.keys", "Help: Keyboard shortcuts", "Help", () => OpenLink(DocsUrl + "GUIDE.md#4-keys-and-the-command-palette"),
            description: "The default keys and how to change them; herd mode's sequences");
        c.Register("help.about", "Help: About OverShell", "Help", () =>
            ShowStatusMessage($"OverShell {App.InformationalVersion} \u00B7 {Environment.ProcessPath} \u00B7 settings {AppPaths.ConfigRoot} \u00B7 state {AppPaths.StateRoot}"),
            description: "Version, executable, where the settings and the state live");
    }

    /// <summary>Opens the file in whatever edits .jsonc here, writing it from the defaults first when it is missing.</summary>
    private void OpenSettingsFile(string path, string resource)
    {
        try
        {
            if (SettingsFiles.WriteDefault(path, resource, "OverShell"))
            {
                ShowStatusMessage($"Wrote {Path.GetFileName(path)} from the defaults; every line equals the default until you change it");
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No editor is associated with .jsonc, or the write failed: the folder is the fallback.
            ShowStatusMessage($"Could not open {Path.GetFileName(path)} ({e.Message}); opening its folder");
            OpenFolder(Path.GetDirectoryName(path) ?? AppPaths.ConfigRoot);
        }
    }

    /// <summary>Writes the built-in defaults as the user's starting point; never over an existing file. Saved edits then reload live.</summary>
    private void InitSettingsFiles()
    {
        IReadOnlyList<string> written;
        try
        {
            written = SettingsFiles.WriteDefaults("OverShell");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatusMessage($"Could not write settings: {e.Message}");
            return;
        }

        ShowStatusMessage(written.Count == 0 ? $"Settings files already exist in {AppPaths.ConfigRoot}" : $"Wrote {string.Join(" and ", written)} to {AppPaths.ConfigRoot}");
        OpenFolder(AppPaths.ConfigRoot);
    }

    // ------------------------------------------------------------ the pickers

    /// <summary>Every knob with its current value; choosing one opens its values.</summary>
    private void OpenSettingsPicker()
    {
        if (_palette is { IsVisible: true })
        {
            _palette.Close();
            return;
        }

        var snapshot = SettingsFiles.Snapshot();
        _palette = PaletteWindow.Picker(this, SettingsGlyph, "No such setting - the rest is in settings.jsonc (Open settings.jsonc)", query =>
        {
            var items = new List<(int Score, int Order, PaletteItem Item)>();
            var order = 0;
            foreach (var knob in SettingsCatalog.Knobs)
            {
                var current = SettingKnob.Display(snapshot.Current(knob.Segments));
                var score = FuzzyMatcher.Score(query.Text, knob.Title, knob.Path, knob.Description);
                if (score is null)
                {
                    continue;
                }

                var captured = knob;
                items.Add((score.Value, order++, new PaletteItem
                {
                    Title = knob.Title,
                    Detail = $"{current}{(snapshot.IsUserSet(knob.Segments) ? string.Empty : " (default)")}  \u00B7  {knob.Description}",
                    Hint = knob.Path,
                    Glyph = SettingsGlyph,
                    Keywords = knob.Path,
                    Invoke = () => OpenValuePicker(captured),
                }));
            }

            return items.OrderByDescending(i => i.Score).ThenBy(i => i.Order).Select(i => i.Item).ToList();
        });
        _palette.Closed += (_, _) => { _palette = null; ActiveTab?.Surface.Focus(); };
    }

    /// <summary>
    /// The values one knob takes, the current one marked; for numbers and text, what is typed
    /// into the box is offered first when it is a value for this setting.
    /// </summary>
    private void OpenValuePicker(SettingKnob knob)
    {
        if (_palette is { IsVisible: true })
        {
            _palette.Close();
        }

        var snapshot = SettingsFiles.Snapshot();
        var current = SettingKnob.Display(snapshot.Current(knob.Segments));
        var choices = knob.Choices.Concat(knob.MoreChoices?.Invoke() ?? []).ToList();
        var hint = knob.Kind switch
        {
            SettingKind.Number => $"type a number{(double.IsFinite(knob.Min) && double.IsFinite(knob.Max) ? $" from {knob.Min:0.##} to {knob.Max:0.##}" : string.Empty)}, or pick one",
            SettingKind.Text => "type a value, or pick one",
            _ => "no such value",
        };
        _palette = PaletteWindow.Picker(this, SettingsGlyph, $"{knob.Title}: {hint}", query =>
        {
            var items = new List<PaletteItem>();
            if (knob.JsonForTyped(query.Text) is { } typedJson)
            {
                var typed = SettingKnob.Display(typedJson);
                items.Add(new PaletteItem
                {
                    Title = $"Use {typed}",
                    Detail = $"{knob.Path} = {typedJson}",
                    Hint = string.Equals(typed, current, StringComparison.OrdinalIgnoreCase) ? "current" : null,
                    Glyph = "\uE70F", // pencil
                    Invoke = () => ApplySetting(knob, typedJson),
                });
            }

            foreach (var choice in choices)
            {
                if (FuzzyMatcher.Score(query.Text, choice.Label, choice.Detail ?? string.Empty) is null)
                {
                    continue;
                }

                var isCurrent = string.Equals(SettingKnob.Display(choice.Json), current, StringComparison.OrdinalIgnoreCase);
                var captured = choice;
                items.Add(new PaletteItem
                {
                    Title = choice.Label,
                    Detail = string.IsNullOrEmpty(choice.Detail) ? knob.Path : $"{choice.Detail}  \u00B7  {knob.Path}",
                    Hint = isCurrent ? "current" : null,
                    Glyph = isCurrent ? "\uE73E" : SettingsGlyph, // check mark
                    Invoke = () => ApplySetting(knob, captured.Json),
                });
            }

            return items;
        });
        _palette.Closed += (_, _) => { _palette = null; ActiveTab?.Surface.Focus(); };
    }

    /// <summary>
    /// Writes the value into settings.jsonc. Nothing is applied here: the file watcher reloads
    /// it within half a second exactly as it would a hand edit, and its status line names the
    /// change (<see cref="_settingChangeNote"/>), so what the palette did and what the file
    /// says can never disagree.
    /// </summary>
    internal bool ApplySetting(SettingKnob knob, string json)
    {
        var display = SettingKnob.Display(json);
        if (!SettingsFiles.Set(knob.Segments, json, "OverShell", out var error))
        {
            ShowStatusMessage($"{knob.Title}: not changed - {error}");
            _trace.Write($"settings: {knob.Path} = {json} not written: {error}");
            return false;
        }

        _settingChangeNote = $"{knob.Title} = {display}";
        _trace.Write($"settings: {knob.Path} = {json} written to {AppPaths.SettingsFile}");
        ShowStatusMessage($"{knob.Title} = {display}  \u00B7  written to settings.jsonc, applying\u2026");
        return true;
    }

    /// <summary>The note for the reload's status line, once.</summary>
    private string? TakeSettingChangeNote()
    {
        var note = _settingChangeNote;
        _settingChangeNote = null;
        return note;
    }

    // ------------------------------------------------------------ first run

    /// <summary>
    /// The first start on this machine - no state, no session, no history yet: a few dim lines
    /// above the first prompt saying how to get around, with the keys as they are bound (a
    /// changed binding shows its own chord), and where the tutorial is. Output into the
    /// terminal, not input: it scrolls away like any text and never reaches the shell (§7.8).
    /// </summary>
    private string? WelcomePreamble()
    {
        if (StartFresh || File.Exists(AppPaths.StateFile) || File.Exists(AppPaths.SessionFile) || SessionHistory.List(AppPaths.StateRoot).Count > 0)
        {
            return null;
        }

        string Key(string command, string fallback) => HintFor(command) ?? fallback;
        var lines = new[]
        {
            "OverShell - one window for your shells and the agents working in them.",
            $"  {Key("palette.commands", "Ctrl+Shift+P"),-18} commands - type to search; every feature is in there",
            $"  {Key("tab.new", "Ctrl+Shift+T"),-18} new tab        {Key("palette.tabs", "Ctrl+Shift+Space"),-18} switch tabs",
            $"  {Key("tab.jumpToAttention", "Ctrl+Shift+J"),-18} jump to the agent that needs you",
            "  Help: Tutorial (in the palette) walks you through the rest in ten minutes.",
        };
        var sb = new System.Text.StringBuilder("\u001b[2m");
        foreach (var line in lines)
        {
            sb.Append(line).Append("\r\n");
        }

        sb.Append("\u001b[0m\u001b[2;3m\u2014 welcome; this note shows on the first start only \u2014\u001b[0m\r\n\r\n");
        _trace.Write("start: first run on this machine - the welcome note goes above the first prompt");
        return sb.ToString();
    }

    /// <summary>For the self-test: the text the welcome note carries, without the escapes.</summary>
    internal static bool LooksLikeWelcome(IEnumerable<string> rows) =>
        rows.Any(r => r.Contains("Help: Tutorial", StringComparison.Ordinal)) && rows.Any(r => r.Contains("this note shows on the first start only", StringComparison.Ordinal));
}
