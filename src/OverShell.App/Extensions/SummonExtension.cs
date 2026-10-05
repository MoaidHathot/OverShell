using OverShell.Core.Extensibility;
using OverShell.Core.Input;

namespace OverShell.App.Extensions;

/// <summary>
/// Global summon (DESIGN.md §12.16): one system-wide hotkey brings OverShell to the front
/// from any application - on the tab that needs you, when one does - and, pressed again
/// while it is in front, puts it away. A quake-style terminal's one trick, as an
/// extension. Settings: <c>extensions.summon</c> (<c>keys</c>, <c>toggle</c>, <c>to</c>).
/// </summary>
public sealed class SummonExtension : IExtension
{
    private IShell _shell = null!;
    private SummonSettings _settings = new();
    private int? _handle;

    public string Id => "summon";

    public sealed class SummonSettings
    {
        /// <summary>The hotkey, in keybinding notation; empty for none. Default: Win+`.</summary>
        public string Keys { get; init; } = "win+backtick";

        /// <summary>Pressed while OverShell is in front: minimize it.</summary>
        public bool Toggle { get; init; } = true;

        /// <summary><c>attention</c>: land on the tab that has waited longest, if any; <c>current</c>: leave the active tab alone.</summary>
        public string To { get; init; } = "attention";
    }

    /// <summary>What the last summon did, for the self-test and the trace.</summary>
    internal string LastAction { get; private set; } = "none";

    internal string? RegistrationError { get; private set; }

    internal bool IsRegistered => _handle is not null;

    public void Initialize(IShell shell)
    {
        _shell = shell;
        _settings = shell.ExtensionSettings<SummonSettings>(Id);

        shell.Commands.Register("window.summon", "Summon OverShell", "Window", Summon,
            description: "Bring the window to the front (on the tab that needs you); again puts it away. Bound system-wide by extensions.summon.keys");

        if (shell.IsReady)
        {
            Register();
        }
        else
        {
            shell.Ready += Register;
        }

        shell.SettingsChanged += () =>
        {
            var previous = _settings;
            _settings = shell.ExtensionSettings<SummonSettings>(Id);
            if (!string.Equals(previous.Keys, _settings.Keys, StringComparison.OrdinalIgnoreCase))
            {
                Unregister();
                Register();
            }
        };
    }

    private void Register()
    {
        RegistrationError = null;
        if (string.IsNullOrWhiteSpace(_settings.Keys))
        {
            return;
        }

        if (!KeyChord.TryParse(_settings.Keys, out var chord))
        {
            RegistrationError = $"extensions.summon.keys '{_settings.Keys}' is not a key chord";
            _shell.Trace($"summon: {RegistrationError}");
            return;
        }

        var (handle, error) = _shell.Ui.RegisterGlobalHotKey(chord, Summon);
        _handle = handle;
        RegistrationError = error;
        _shell.Trace(handle is null ? $"summon: not registered - {error}" : $"summon: {chord} registered system-wide");
        if (error is not null)
        {
            _shell.Ui.Status($"Summon key {chord}: {error}");
        }
    }

    private void Unregister()
    {
        if (_handle is { } handle)
        {
            _shell.Ui.UnregisterGlobalHotKey(handle);
            _handle = null;
        }
    }

    /// <summary>The hotkey's work: front and attention, or away.</summary>
    internal void Summon()
    {
        if (_settings.Toggle && _shell.Ui.IsForeground)
        {
            _shell.Ui.Minimize();
            LastAction = "minimized";
            _shell.Trace("summon: in front already - minimized");
            return;
        }

        var landed = "current";
        if (string.Equals(_settings.To, "attention", StringComparison.OrdinalIgnoreCase))
        {
            // The tab that has waited longest, blocked before unseen-done - quietly: a summon
            // with nothing waiting is not an occasion for a status message.
            var waiting = _shell.Tabs
                .Where(t => t.NeedsAttention)
                .OrderBy(t => Core.Herd.HerdOrdering.AttentionRank(t.State, t.Unread))
                .ThenBy(t => t.AttentionSince ?? DateTimeOffset.MaxValue)
                .FirstOrDefault();
            if (waiting is not null)
            {
                _shell.Activate(waiting);
                landed = waiting.Label;
            }
        }

        _shell.Ui.BringToFront();
        LastAction = $"front ({landed})";
        _shell.Trace($"summon: to the front, tab {landed}");
    }

    public void Dispose()
    {
        Unregister();
        if (_shell is not null)
        {
            _shell.Ready -= Register;
        }
    }
}
