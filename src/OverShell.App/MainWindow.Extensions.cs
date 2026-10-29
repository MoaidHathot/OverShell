using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OverShell.App.Extensibility;
using OverShell.Config;
using OverShell.Core;
using OverShell.Core.Agents;
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

    internal AgentRules Rules => _rules;

    /// <summary>The slot under the terminal where bars live: find, prompt, and extensions' own.</summary>
    internal StackPanel Bars => BarsHost;

    /// <summary>The element the terminal body fills.</summary>
    internal System.Windows.FrameworkElement TerminalArea => TerminalHost;

    /// <summary>The margin ring's opacity when the body is composed (12.20), else null. For the self-test.</summary>
    internal double? TerminalBodyTintOpacity => TerminalBodyTint.Visibility == System.Windows.Visibility.Visible ? TerminalBodyTint.Opacity : null;

    /// <summary>The caption icon as shown: the frame's pixel size, the element's size in DIPs and the window's scale. For the self-test.</summary>
    internal (int PixelWidth, double ShownDip, double Scale) CaptionIconState =>
        (CaptionIcon.Source is System.Windows.Media.Imaging.BitmapSource b ? b.PixelWidth : 0, CaptionIcon.Width, System.Windows.Media.VisualTreeHelper.GetDpi(CaptionIcon).DpiScaleX);
    /// <summary>The margin ring's thickness when the body is composed, else null. For the self-test.</summary>
    internal System.Windows.Thickness? TerminalBodyTintThickness => TerminalBodyTint.Visibility == System.Windows.Visibility.Visible ? TerminalBodyTint.BorderThickness : null;

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

        ApplyEndpointControl();

        // Extensions declared their default keys during Initialize; the map is rebuilt with them beneath the user's file.
        LoadKeybindings();
        Closed += (_, _) =>
        {
            _extensions.Dispose();
            _shell.DisposeHotKeys();
        };
    }

    private void ApplyEndpointControl()
    {
        if (_endpoint is not { } endpoint || (endpoint.Control is not null) == _settings.Endpoint.Control) return;
        if (_settings.Endpoint.Control)
        {
            endpoint.Control = new OverShell.Core.Integrations.ShellControl(_shell);
            try
            {
                File.WriteAllText(AppPaths.EndpointFile, OverShell.Core.Integrations.EndpointInfo.Current(endpoint.BaseUrl, endpoint.Token).ToJson().ToJsonString());
                _trace.Write($"endpoint: control API on; {AppPaths.EndpointFile} written");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _trace.Write($"endpoint: control API on, but endpoint.json could not be written: {e.Message}");
            }
        }
        else
        {
            endpoint.Control = null;
            ForgetEndpointFile();
            _trace.Write("endpoint: control API off (endpoint.control)");
        }
    }

    /// <summary>Removes endpoint.json at exit - only when it is this run's, so a second window's file is left alone.</summary>
    private void ForgetEndpointFile()
    {
        try
        {
            if (OverShell.Core.Integrations.EndpointInfo.Read(AppPaths.EndpointFile) is { } info && info.Pid == Environment.ProcessId)
            {
                File.Delete(AppPaths.EndpointFile);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _trace.Write($"endpoint: endpoint.json not removed: {e.Message}");
        }
    }

    /// <summary>Opens a tab the way an extension asks for one: profile by name, directory, label, group, a command typed at the prompt.</summary>
    internal TerminalTab? OpenTabFor(TabRequest request) => OpenTabFor(request, out _);

    internal TerminalTab? OpenTabFor(TabRequest request, out string? error, Core.Tabs.NavigationPoint? returnTo = null)
    {
        if (request.Floating && !request.Activate && ActiveTerminalWindow() is Chrome.TearOffWindow { IsFloating: true })
        {
            error = "A floating terminal has focus; activate the replacement or hide the current float first";
            return null;
        }
        var profile = _catalog.ResolveProfile(request.Profile ?? (request.Scratch ? _settings.Scratch.Profile : null), out error);
        if (profile is null)
        {
            error ??= "No launchable profile";
            _trace.Write($"open tab: {error}");
            ShowStatusMessage(error);
            return null;
        }

        var aliasError = Core.Tabs.TabAddressing.Validate(request.Alias);
        if (aliasError is not null || !string.IsNullOrWhiteSpace(request.Alias) && _addresses.Resolve(new Core.Tabs.TabSelector(Alias: request.Alias.Trim())).TabId is not null)
        {
            error = aliasError ?? $"Alias '{request.Alias}' already belongs to an open tab";
            ShowStatusMessage(error);
            return null;
        }
        if (request.Process is { } process && (process.Validate() is not null || !string.IsNullOrWhiteSpace(request.Command)))
        {
            error = process.Validate() ?? "Specify either a process or a typed shell command";
            ShowStatusMessage(error);
            return null;
        }
        var startingDirectory = request.WorkingDirectory ?? (request.Scratch && _settings.Scratch.InheritDirectory ? TargetTab?.WorkingDirectory : null);
        if (startingDirectory is { } cwd)
        {
            cwd = Environment.ExpandEnvironmentVariables(cwd);
            if (Directory.Exists(cwd))
            {
                profile = profile with { StartingDirectory = cwd };
            }
            else
            {
                error = $"Directory '{cwd}' does not exist";
                ShowStatusMessage(error);
                return null;
            }
        }
        var processCommandLine = request.Process is { } raw
            ? (raw with { Executable = Environment.ExpandEnvironmentVariables(raw.Executable) }).CommandLine : null;
        var lifetime = new Core.Tabs.TabLifetime(request.Scratch,
            request.Restore ?? (!request.Scratch || _settings.Scratch.Restore), request.History ?? (!request.Scratch || _settings.Scratch.History),
            request.OnExit ?? (request.Scratch ? _settings.Scratch.EffectiveExit : Core.Tabs.TabExitBehavior.Keep));
        if (!Enum.IsDefined(lifetime.OnExit))
        {
            error = "Unknown exit policy";
            return null;
        }
        var origin = returnTo ?? Navigation.Capture();
        var tab = AddTab(profile, request.Activate && !request.Floating, userLabel: request.Label, lifetime: lifetime, oneShot: request.Process is not null, processCommandLine: processCommandLine, initialize: created =>
        {
            if (!string.IsNullOrWhiteSpace(request.Group)) created.Group = request.Group;
            if (!string.IsNullOrWhiteSpace(request.Alias)) _addresses.Assign(created.Id, request.Alias);
            if (!string.IsNullOrWhiteSpace(request.Command)) created.ScheduleResume(request.Command.Trim());
            if (!string.IsNullOrWhiteSpace(request.Prompt)) created.ScheduleFirstPrompt(request.Prompt.Trim());
        });
        if (request.Floating)
        {
            var shown = Presentations.ShowFloating(tab.Id, returnTo: origin, activate: request.Activate);
            if (!shown.Success)
            {
                error = shown.Error;
                // Failed creation must not leave an orphaned process or a misleading history entry.
                tab.SetLifetime(lifetime with { History = false });
                CloseTab(tab);
                return null;
            }
        }
        else if (request.Activate) PresentNewTab(tab);
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
        _keybindings = KeybindingMap.Load(File.Exists(AppPaths.KeybindingsFile) ? AppPaths.KeybindingsFile : null, _shell?.ExtensionDefaults,
            _runtimeShortcuts.Items.Where(i => !i.Global).Select(i => i.Binding));
        _tabStrip?.SetActionHints(HintFor("tab.new"), HintFor("tab.newWithProfile"));
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

        // Route editing commands to the text control even when the user chose a different
        // chord. Returning false only works for WPF's built-in keys, not Ctrl+Shift+V or
        // custom bindings. Other clipboard commands (such as opening the files folder)
        // still run normally while a text box has focus.
        if (TextInputHasFocus())
        {
            if (CurrentPrompt.TryClipboardCommand(command) || RouteTextClipboard(command, Keyboard.FocusedElement)) return true;
        }

        // A chord from a tear-off aims at that window's tab.
        _commandTarget = TerminalWindowForRoot(_shortcuts.CurrentRoot)?.Tab;
        try
        {
            if (command.StartsWith("tab.newProfile.", StringComparison.OrdinalIgnoreCase) && _commands.Find(command) is null)
            {
                // A binding can outlive a profile removed from the source. Explain that
                // request rather than leaking its shortcut into the foreground shell.
                OpenProfileTab(command["tab.newProfile.".Length..]);
                return true;
            }
            var taken = _commands.TryExecute(command, decision.Command.Arguments);

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

    internal static bool RouteTextClipboard(string command, IInputElement? target)
    {
        if (target is not System.Windows.Controls.Primitives.TextBoxBase) return false;
        var editing = command.ToLowerInvariant() switch
        {
            "clipboard.copy" or "clipboard.copyifselection" => ApplicationCommands.Copy,
            "clipboard.paste" => ApplicationCommands.Paste,
            _ => null,
        };
        if (editing is null) return false;
        if (editing.CanExecute(null, target)) editing.Execute(null, target);
        return true;
    }

    /// <summary>A pending sequence that has waited past the timeout is dropped; the heartbeat calls this.</summary>
    private void ExpireSequences(DateTimeOffset now)
    {
        if (_sequences.Expire(now))
        {
            _shell.RaisePendingChanged(_sequences.Pending);
        }
    }
}
