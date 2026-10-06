using System.Text.RegularExpressions;
using OverShell.Core.Agents;
using OverShell.Core.Extensibility;
using OverShell.Core.Input;
using OverShell.Core.Notifications;

namespace OverShell.App.Extensions;

/// <summary>
/// Three small triage helpers (DESIGN.md §12.17), each a tab property that lives in
/// <see cref="ITab.Properties"/> and so survives a restart with the tab:
/// <list type="bullet">
/// <item><b>Mute</b> (<c>tab.mute</c>): a noisy tab's notifications are dropped - the state
/// still shows on its item, nothing fires for it.</item>
/// <item><b>Watch</b> (<c>tab.watch</c>): a regular expression over the tab's screen; when a
/// row starts matching, a notification fires once - "BUILD SUCCEEDED" in a plain shell,
/// a word in an agent's output.</item>
/// <item><b>Auto-advance</b> (<c>extensions.triage.autoAdvance</c>, off): when the blocked tab
/// you are on moves on, jump to the next tab that waits.</item>
/// </list>
/// </summary>
public sealed class TriageExtension : IExtension
{
    private const string MuteKey = "triage.muted";
    private const string WatchKey = "triage.watch";

    private IShell _shell = null!;
    private TriageSettings _settings = new();
    private readonly Dictionary<string, (Regex Regex, string? LastHit)> _watches = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "triage";

    public sealed class TriageSettings
    {
        /// <summary>When the blocked tab you are looking at moves on, jump to the next waiting tab.</summary>
        public bool AutoAdvance { get; init; }
    }

    public void Initialize(IShell shell)
    {
        _shell = shell;
        _settings = shell.ExtensionSettings<TriageSettings>(Id);

        shell.Commands.Register("tab.mute", "Mute / unmute this tab's notifications", "Agents", () =>
        {
            if (shell.TargetTab is { } tab)
            {
                var muted = !IsMuted(tab);
                SetMuted(tab, muted);
                shell.Ui.Status(muted ? $"{tab.Label}: muted - its state still shows, nothing fires" : $"{tab.Label}: notifications back on");
            }
        }, () => shell.TargetTab is not null, "The dot and badge still show; toasts, sounds and the taskbar stay quiet for it");

        shell.Commands.Register("tab.watch", "Watch this tab for text (regex)", "Agents", () => _ = WatchPromptAsync(), () => shell.TargetTab is not null,
            "A pattern over the screen; one notification when a row starts matching. Empty removes the watch");

        shell.Keys.AddDefaults(
        [
            new Keybinding($"{HerdModeExtension.Leader} m", "tab.mute"),
            new Keybinding($"{HerdModeExtension.Leader} w", "tab.watch"),
        ]);

        shell.NotificationFilter = e => !(shell.Find(e.TabId) is { } tab && IsMuted(tab));
        shell.Heartbeat += OnHeartbeat;
        shell.TabStateChanged += OnStateChanged;
        shell.TabOpened += tab => RebuildWatch(tab);
        shell.TabClosed += tab => _watches.Remove(tab.Id);
        shell.SettingsChanged += () => _settings = shell.ExtensionSettings<TriageSettings>(Id);

        foreach (var tab in shell.Tabs)
        {
            RebuildWatch(tab);
        }
    }

    public static bool IsMuted(ITab tab) => tab.Properties.TryGetValue(MuteKey, out var v) && v == "true";

    public static string? WatchPattern(ITab tab) => tab.Properties.TryGetValue(WatchKey, out var v) && v.Length > 0 ? v : null;

    internal void SetMuted(ITab tab, bool muted)
    {
        if (muted)
        {
            tab.Properties[MuteKey] = "true";
        }
        else
        {
            tab.Properties.Remove(MuteKey);
        }

        _shell.Trace($"[{tab.Id}] {(muted ? "muted" : "unmuted")}");
        _shell.TabPropertiesChanged(tab);
    }

    internal bool SetWatch(ITab tab, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            tab.Properties.Remove(WatchKey);
            _watches.Remove(tab.Id);
            _shell.TabPropertiesChanged(tab);
            return true;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            return false;
        }

        tab.Properties[WatchKey] = pattern;
        RebuildWatch(tab);
        _shell.TabPropertiesChanged(tab);
        return true;
    }

    private async Task WatchPromptAsync()
    {
        if (_shell.TargetTab is not { } tab)
        {
            return;
        }

        var text = await _shell.Ui.PromptAsync($"Watch {tab.Label} for (regex; empty removes)", WatchPattern(tab) ?? string.Empty);
        if (text is null)
        {
            return;
        }

        if (!SetWatch(tab, text))
        {
            _shell.Ui.Status($"Not a valid regular expression: {text}");
            return;
        }

        _shell.Ui.Status(text.Trim().Length == 0 ? $"{tab.Label}: watch removed" : $"{tab.Label}: watching for /{text}/");
    }

    private void RebuildWatch(ITab tab)
    {
        var pattern = WatchPattern(tab);
        if (pattern is null)
        {
            _watches.Remove(tab.Id);
            return;
        }

        try
        {
            _watches[tab.Id] = (new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), null);
        }
        catch (ArgumentException)
        {
            _watches.Remove(tab.Id);
        }
    }

    /// <summary>Every heartbeat: a watched tab's rows are checked; a row that starts matching fires once.</summary>
    private void OnHeartbeat(DateTimeOffset now)
    {
        if (_watches.Count == 0)
        {
            return;
        }

        foreach (var tab in _shell.Tabs)
        {
            if (!_watches.TryGetValue(tab.Id, out var watch))
            {
                continue;
            }

            if (now.Second % 2 == 0)
            {
                tab.RequestScreen();
            }

            string? hit = null;
            for (var i = tab.ScreenRows.Count - 1; i >= 0; i--)
            {
                var row = tab.ScreenRows[i];
                if (row.Length > 0 && watch.Regex.IsMatch(row))
                {
                    hit = row.Trim();
                    break;
                }
            }

            if (hit is not null && hit != watch.LastHit)
            {
                _watches[tab.Id] = (watch.Regex, hit);
                _shell.Trace($"[{tab.Id}] watch /{watch.Regex}/ matched: {hit}");
                _shell.Ui.Notify(NotificationKind.Done, $"Watch: {tab.Label}", hit, tab.Id);
            }
            else if (hit is null && watch.LastHit is not null)
            {
                // The row scrolled away; the next appearance counts again.
                _watches[tab.Id] = (watch.Regex, null);
            }
        }
    }

    /// <summary>Auto-advance: the blocked tab in front moved on -> the next waiting tab.</summary>
    private void OnStateChanged(ITab tab, AgentTransition transition)
    {
        if (!_settings.AutoAdvance || !ReferenceEquals(tab, _shell.ActiveTab))
        {
            return;
        }

        if (transition.From is AgentState.Blocked && transition.To is not (AgentState.Blocked or AgentState.Error))
        {
            _shell.Post(() =>
            {
                if (_shell.Tabs.Any(t => t.State is AgentState.Blocked or AgentState.Error && !ReferenceEquals(t, _shell.ActiveTab)))
                {
                    _shell.Commands.TryExecute("tab.nextBlocked");
                }
            });
        }
    }

    public void Dispose()
    {
        if (_shell is not null)
        {
            _shell.Heartbeat -= OnHeartbeat;
            _shell.TabStateChanged -= OnStateChanged;
            _shell.NotificationFilter = null;
        }
    }
}
