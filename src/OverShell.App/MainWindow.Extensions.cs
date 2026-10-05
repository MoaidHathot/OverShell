using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using OverShell.App.Extensibility;
using OverShell.Config;
using OverShell.Core;
using OverShell.Core.Extensibility;
using OverShell.Core.Input;
using OverShell.Core.Settings;

namespace OverShell.App;

/// <summary>
/// The window as a host for extensions (DESIGN.md §12.16): the <see cref="IShell"/> surface,
/// the extension lifecycle, and key sequences - chords are fed to a
/// <see cref="KeySequenceDispatcher"/> rather than looked up one at a time, so a leader key
/// and the sequences behind it are the engine's, and the hint bar that shows them is an
/// extension's.
/// </summary>
public partial class MainWindow
{
    private ShellHost _shell = null!;
    private ExtensionHost _extensions = null!;
    private KeySequenceDispatcher _sequences = null!;
    private readonly Dictionary<(Key Key, ModifierKeys Modifiers), KeyChord> _canonicalChords = [];

    /// <summary>The settings in force (reloaded live).</summary>
    internal AppSettings CurrentSettings => _settings;

    internal Diagnostics.TraceLog Trace => _trace;

    internal KeybindingMap Keybindings => _keybindings;

    /// <summary>The slot under the terminal where bars live: find, prompt, and extensions' own.</summary>
    internal StackPanel Bars => BarsHost;

    /// <summary>The element the terminal body fills.</summary>
    internal System.Windows.FrameworkElement TerminalArea => TerminalHost;

    /// <summary>The chords of a key sequence pressed so far.</summary>
    internal IReadOnlyList<KeyChord> PendingChords => _sequences?.Pending ?? [];

    /// <summary>The shell as extensions see it; for the self-test.</summary>
    internal IShell Shell => _shell;

    internal ExtensionHost Extensions => _extensions;

    private void InitializeExtensions()
    {
        _shell = new ShellHost(this);
        _extensions = new ExtensionHost();
        _extensions.Load(_shell, ExtensionHost.BuiltIns);

        // Extensions declared their default keys during Initialize; the map is rebuilt with them beneath the user's file.
        LoadKeybindings();
        Closed += (_, _) => _extensions.Dispose();
    }

    /// <summary>Opens a tab the way an extension asks for one: profile by name, directory, label, group, a command typed at the prompt.</summary>
    internal TerminalTab? OpenTabFor(TabRequest request)
    {
        var profile = ProfileNamed(request.Profile);
        if (profile is null)
        {
            _trace.Write($"open tab: no launchable profile for '{request.Profile}' and no default");
            return null;
        }

        if (request.WorkingDirectory is { } cwd)
        {
            cwd = Environment.ExpandEnvironmentVariables(cwd);
            if (Directory.Exists(cwd))
            {
                profile = profile with { StartingDirectory = cwd };
            }
            else
            {
                _trace.Write($"open tab: directory '{cwd}' is not there; the profile's own is used");
            }
        }

        var tab = AddTab(profile, request.Activate);
        if (!string.IsNullOrWhiteSpace(request.Label))
        {
            tab.UserLabel = request.Label;
        }

        if (!string.IsNullOrWhiteSpace(request.Group))
        {
            tab.Group = request.Group;
        }

        if (!string.IsNullOrWhiteSpace(request.Command))
        {
            tab.ScheduleResume(request.Command.Trim());
        }

        return tab;
    }

    // ------------------------------------------------------------ key sequences

    /// <summary>
    /// Rebuilds the dispatcher's map: the embedded defaults, the extensions' defaults, the
    /// user's file. Chord names are resolved to WPF keys once, because <c>Key</c> has aliases
    /// (Return/Enter, Next/PageDown) and only the enum value tells them apart.
    /// </summary>
    private void LoadKeybindings()
    {
        _keybindings = KeybindingMap.Load(File.Exists(AppPaths.KeybindingsFile) ? AppPaths.KeybindingsFile : null, _shell?.ExtensionDefaults);
        foreach (var problem in _keybindings.Problems)
        {
            _trace.Write($"keybindings: {problem}");
        }

        _canonicalChords.Clear();
        foreach (var sequence in _keybindings.Bindings.Keys)
        {
            foreach (var chord in sequence.Chords)
            {
                if (Enum.TryParse<Key>(chord.Key, ignoreCase: true, out var key))
                {
                    _canonicalChords[(key, ToModifiers(chord.Modifiers))] = chord;
                }
                else
                {
                    _trace.Write($"keybindings: '{chord}' names no WPF key");
                }
            }
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Max(300, _settings.Keys.SequenceTimeoutMs));
        if (_sequences is null)
        {
            _sequences = new KeySequenceDispatcher(_keybindings, timeout);
        }
        else
        {
            _sequences.Map = _keybindings;
            _sequences.Timeout = timeout;
            _sequences.Cancel();
            _shell?.RaisePendingChanged(_sequences.Pending);
        }
    }

    private static ModifierKeys ToModifiers(ChordModifiers m)
    {
        var result = ModifierKeys.None;
        if (m.HasFlag(ChordModifiers.Control)) result |= ModifierKeys.Control;
        if (m.HasFlag(ChordModifiers.Shift)) result |= ModifierKeys.Shift;
        if (m.HasFlag(ChordModifiers.Alt)) result |= ModifierKeys.Alt;
        if (m.HasFlag(ChordModifiers.Win)) result |= ModifierKeys.Windows;
        return result;
    }

    private static ChordModifiers ToChordModifiers(ModifierKeys m)
    {
        var result = ChordModifiers.None;
        if (m.HasFlag(ModifierKeys.Control)) result |= ChordModifiers.Control;
        if (m.HasFlag(ModifierKeys.Shift)) result |= ChordModifiers.Shift;
        if (m.HasFlag(ModifierKeys.Alt)) result |= ChordModifiers.Alt;
        if (m.HasFlag(ModifierKeys.Windows)) result |= ChordModifiers.Win;
        return result;
    }

    /// <summary>
    /// The router saw a chord: feed it to the sequence dispatcher and run what it says.
    /// Swallow the key when a command took it or a sequence is in progress; let it through
    /// to the terminal otherwise.
    /// </summary>
    private bool OnChord(Key key, ModifierKeys modifiers)
    {
        if (!_canonicalChords.TryGetValue((key, modifiers), out var chord))
        {
            chord = new KeyChord(ToChordModifiers(modifiers), key.ToString());
        }

        // An extension that owns the keyboard for the moment (an open switcher) sees the chord first.
        if (!_sequences.IsPending && _shell.TryIntercept(chord))
        {
            return true;
        }

        var wasPending = _sequences.IsPending;
        var decision = _sequences.Feed(chord, DateTimeOffset.Now);
        if (wasPending || decision.Outcome is KeyOutcome.Pending)
        {
            _shell.RaisePendingChanged(decision.Pending);
        }

        switch (decision.Outcome)
        {
            case KeyOutcome.Unbound:
                return false;
            case KeyOutcome.Pending:
            case KeyOutcome.Rejected:
            case KeyOutcome.Cancelled:
                return true;
        }

        var command = decision.Command!.Command;

        // Ctrl+C / Ctrl+V while typing in the prompt bar edit the prompt, not the terminal.
        if (TextInputHasFocus() && command.StartsWith("clipboard.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A chord from a tear-off aims at that window's tab.
        _commandTarget = TearOffForRoot(_shortcuts.CurrentRoot)?.Tab;
        try
        {
            var taken = _commands.TryExecute(command);

            // A key that ran inside a sequence is always consumed, even by a command that declined.
            return taken || wasPending || decision.Command.Keys.Length > 1;
        }
        finally
        {
            _commandTarget = null;
        }
    }

    /// <summary>What the router would do with a chord - for in-process diagnostics, which inject no keys.</summary>
    internal bool DispatchChord(Key key, ModifierKeys modifiers) => OnChord(key, modifiers);

    /// <summary>A pending sequence that has waited past the timeout is dropped; the heartbeat calls this.</summary>
    private void ExpireSequences(DateTimeOffset now)
    {
        if (_sequences.Expire(now))
        {
            _shell.RaisePendingChanged(_sequences.Pending);
        }
    }
}
