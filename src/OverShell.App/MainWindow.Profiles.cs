using System.Windows;
using OverShell.App.Chrome;
using OverShell.Config;
using OverShell.Core.Search;
using OverShell.Core.Settings;

namespace OverShell.App;

public partial class MainWindow
{
    private const string ProfileGlyph = "\uE756";
    private PaletteWindow? _profilePicker;

    internal static string ProfileCommandId(TerminalProfile profile) => "tab.newProfile." + profile.Id;

    private string ProfileTitle(TerminalProfile profile) =>
        _catalog.Profiles.Count(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)) > 1
            ? $"[{profile.Id}] {profile.Name}" : profile.Name;

    private string ProfileSourceDescription =>
        $"{_catalog.Location.Source} · {_catalog.Location.Origin} · {_catalog.SettingsPath ?? "generated profiles"}\nDefault: {_catalog.DefaultProfile?.Name}" +
        (_catalog.Diagnostics.Count == 0 ? string.Empty : "\n" + string.Join("\n", _catalog.Diagnostics));

    private void RegisterProfileCommands()
    {
        foreach (var stale in _commands.All.Where(c => c.Id.StartsWith("tab.newProfile.", StringComparison.Ordinal)).Select(c => c.Id).ToArray())
            _commands.Remove(stale);
        foreach (var profile in _catalog.Profiles)
        {
            var id = profile.Id;
            _commands.Register(ProfileCommandId(profile), $"New tab: {ProfileTitle(profile)}", "Profiles", () => OpenProfileTab(id),
                description: $"{profile.CommandLine} · {profile.Source ?? "static"} · {id}");
        }
    }

    /// <summary>The window the command came from, including a detached terminal or its owned palette.</summary>
    private Window CommandOwner() =>
        _tearOffs.FirstOrDefault(w => ReferenceEquals(w.Tab, _commandTarget))
        ?? _tearOffs.FirstOrDefault(w => w.IsActive)
        ?? (Window)this;

    private void RefocusOwner(Window owner)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!owner.IsActive) return;
            if (owner is TearOffWindow tearOff && Tabs.Contains(tearOff.Tab)) tearOff.Tab.Surface.Focus();
            else if (_settings.ViewFor(_viewId).Content == ViewContent.Terminal) ActiveTab?.Surface.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OpenProfilePicker()
    {
        var owner = CommandOwner();
        _palette?.Close();
        var palette = PaletteWindow.Picker(owner, ProfileGlyph, "No matching profile", BuildProfileItems,
            literalQuery: true, context: ProfileSourceDescription);
        palette.Title = "New tab with profile — OverShell";
        _profilePicker = _palette = palette;
        palette.Closed += (_, _) =>
        {
            if (ReferenceEquals(_palette, palette)) _palette = null;
            if (ReferenceEquals(_profilePicker, palette)) _profilePicker = null;
            RefocusOwner(owner);
        };
    }

    internal IReadOnlyList<PaletteItem> BuildProfileItems(PaletteQuery query) => _catalog.Profiles
        .Select((profile, order) => (Profile: profile, Order: order,
            Score: FuzzyMatcher.Score(query.Text, profile.Name, profile.Id, profile.CommandLine, profile.Source, _catalog.Location.Source)))
        .Where(row => row.Score is not null)
        .OrderByDescending(row => row.Score).ThenBy(row => row.Order)
        .Select(row => new PaletteItem
        {
            Title = ProfileTitle(row.Profile),
            Detail = $"{row.Profile.CommandLine} · {row.Profile.Source ?? "static"} · {row.Profile.Id}",
            Hint = row.Profile.Id == _catalog.DefaultProfile?.Id ? "default" : HintFor(ProfileCommandId(row.Profile)),
            Glyph = ProfileGlyph,
            Invoke = () => OpenProfileTab(row.Profile.Id),
        }).ToList();

    private void OpenProfileTab(string? nameOrId)
    {
        var profile = _catalog.ResolveProfile(nameOrId, out var error);
        if (profile is null)
        {
            ShowStatusMessage(error ?? "No launchable profile");
            _trace.Write($"profiles: {error}");
            return;
        }
        NewTab(profile);
    }

    private SettingKnob ProfileKnob(SettingKnob knob) => knob.Path == "profiles.defaultProfile" ? knob with
    {
        MoreChoices = () => _catalog.Profiles.Select(profile => new SettingChoice(JsoncEdit.ToJson(profile.Id), ProfileTitle(profile),
            $"{profile.CommandLine} · {profile.Id}")),
        Validate = text => _catalog.ResolveProfile(text, out _) is not null ? JsoncEdit.ToJson(text) : null,
    } : knob;

    private bool ProfileChoiceIsCurrent(SettingKnob knob, string choice, string current) =>
        knob.Path == "profiles.defaultProfile" && choice != "null" && current != knob.NullDisplay &&
        _catalog.ResolveProfile(current, out _) is { } profile && JsoncEdit.ToJson(profile.Id) == choice;
}
