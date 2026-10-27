using System.IO;
using System.Windows;
using System.Windows.Threading;
using OverShell.App.Chrome;
using OverShell.Config;
using OverShell.Core;
using OverShell.Core.Search;
using OverShell.Core.Settings;

namespace OverShell.App;

public partial class MainWindow
{
    private const string ProfileGlyph = "\uE756";
    private PaletteWindow? _profilePicker;
    private FileSystemWatcher? _profileWatcher;
    private DispatcherTimer? _profileReloadTimer;
    private string? _profileWatchPath;
    private int _profileReloadVersion;
    private bool _profilesStopped;
    private ProfileSettings? _requestedProfileSettings;

    internal string? ProfileReloadError { get; private set; }
    internal Task ProfileReloadTask { get; private set; } = Task.CompletedTask;
    internal Func<ProfileSettings, ProfileCatalog> ProfileLoader { get; set; } = options => ProfileCatalog.Load(options, AppPaths.ConfigRoot);

    internal static string ProfileCommandId(TerminalProfile profile) => "tab.newProfile." + profile.Id;

    private string ProfileTitle(TerminalProfile profile) =>
        _catalog.Profiles.Count(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)) > 1
            ? $"[{profile.Id}] {profile.Name}" : profile.Name;

    private string ProfileSourceDescription =>
        $"{_catalog.Location.Source} · {_catalog.Location.Origin} · {_catalog.SettingsPath ?? "generated profiles"}\nDefault: {_catalog.DefaultProfile?.Name}" +
        (_catalog.Diagnostics.Count == 0 ? string.Empty : "\n" + string.Join("\n", _catalog.Diagnostics)) +
        (ProfileReloadError is null ? string.Empty : "\nReload failed; showing the last loaded profiles: " + ProfileReloadError);

    private void InitializeProfiles()
    {
        _requestedProfileSettings = _settings.Profiles;
        WatchProfileSource(_catalog.SettingsPath);
    }

    private void RequestProfileReload(bool force = false)
    {
        if (_profilesStopped || _settings.HasLoadError || (!force && _requestedProfileSettings == _settings.Profiles)) return;
        _requestedProfileSettings = _settings.Profiles;
        ProfileReloadTask = ReloadProfilesAsync(++_profileReloadVersion, _settings.Profiles, ProfileLoader);
    }

    private async Task ReloadProfilesAsync(int version, ProfileSettings options, Func<ProfileSettings, ProfileCatalog> load)
    {
        try
        {
            // Generators may probe installed software. Keep their I/O off the UI thread;
            // a later source/default edit owns the result even if an older load finishes last.
            var catalog = await Task.Run(() => load(options));
            if (_profilesStopped || version != _profileReloadVersion) return;
            WatchProfileSource(catalog.SettingsPath);
            ProfileReloadError = catalog.SettingsError;
            if (ProfileReloadError is null)
            {
                _catalog = catalog;
                Raise(nameof(Catalog));
                RegisterProfileCommands();
                _trace.Write($"profiles: {ProfileSourceDescription}");
                ShowStatusMessage($"Profiles reloaded: {catalog.Location.Source} · default {catalog.DefaultProfile?.Name}" +
                    (catalog.Diagnostics.Count == 0 ? string.Empty : " · " + string.Join("; ", catalog.Diagnostics)));
            }
            else
            {
                _trace.Write($"profiles: reload failed: {ProfileReloadError}");
                ShowStatusMessage($"Profiles: {ProfileReloadError}; using the last loaded profiles");
            }
            _profilePicker?.SetContext(ProfileSourceDescription);
            _palette?.RefreshItems();
        }
        catch (Exception e)
        {
            if (_profilesStopped || version != _profileReloadVersion) return;
            ProfileReloadError = e.Message;
            _trace.Write($"profiles: reload failed: {e}");
            ShowStatusMessage($"Profiles could not reload: {e.Message}");
            _palette?.RefreshItems();
        }
    }

    private void WatchProfileSource(string? path)
    {
        if (string.Equals(path, _profileWatchPath, StringComparison.OrdinalIgnoreCase) && _profileWatcher is not null) return;
        _profileWatcher?.Dispose();
        _profileWatcher = null;
        _profileWatchPath = path;
        if (path is null) return;
        try
        {
            var parent = Path.GetDirectoryName(path);
            var directory = parent;
            // Watch an existing ancestor when a configured file/directory is missing, so
            // creating it can recover without a restart or a second settings edit.
            while (directory is not null && !Directory.Exists(directory)) directory = Path.GetDirectoryName(directory);
            if (directory is null) return;
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = !string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase),
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            void Changed(string changedPath)
            {
                if (!string.Equals(path, changedPath, StringComparison.OrdinalIgnoreCase) &&
                    !path.StartsWith(changedPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (_profilesStopped || !string.Equals(path, _profileWatchPath, StringComparison.OrdinalIgnoreCase)) return;
                    _profileReloadTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
                    _profileReloadTimer.Tick -= OnProfileReloadTimer;
                    _profileReloadTimer.Tick += OnProfileReloadTimer;
                    _profileReloadTimer.Stop();
                    _profileReloadTimer.Start();
                });
            }
            watcher.Changed += (_, e) => Changed(e.FullPath);
            watcher.Created += (_, e) => Changed(e.FullPath);
            watcher.Deleted += (_, e) => Changed(e.FullPath);
            watcher.Renamed += (_, e) => { Changed(e.OldFullPath); Changed(e.FullPath); };
            watcher.Error += (_, e) => Dispatcher.BeginInvoke(() =>
            {
                if (_profilesStopped || !ReferenceEquals(_profileWatcher, watcher)) return;
                _trace.Write($"profiles: file watcher: {e.GetException().Message}; reattaching");
                watcher.Dispose();
                _profileWatcher = null;
                WatchProfileSource(path);
                RequestProfileReload(force: true);
            });
            _profileWatcher = watcher;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _profileWatcher?.Dispose();
            _profileWatcher = null;
            _trace.Write($"profiles: could not watch '{path}': {e.Message}");
        }
    }

    private void OnProfileReloadTimer(object? sender, EventArgs e)
    {
        _profileReloadTimer?.Stop();
        RequestProfileReload(force: true);
    }

    private void StopProfiles()
    {
        _profilesStopped = true;
        ++_profileReloadVersion;
        _profileReloadTimer?.Stop();
        _profileWatcher?.Dispose();
    }

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
        palette.ContextSource = () => ProfileSourceDescription;
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

    private bool ProfileChoiceIsCurrent(SettingKnob knob, string choice) =>
        knob.Path == "profiles.defaultProfile" && choice != "null" && !string.IsNullOrWhiteSpace(_settings.Profiles.DefaultProfile) &&
        _catalog.ResolveProfile(_settings.Profiles.DefaultProfile, out _) is { } profile && JsoncEdit.ToJson(profile.Id) == choice;
}
