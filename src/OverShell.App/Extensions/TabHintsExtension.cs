using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using OverShell.Core.Extensibility;
using OverShell.Core.Input;
using OverShell.Core.Settings;
using OverShell.Core.Tabs;

namespace OverShell.App.Extensions;

/// <summary>Automatic tab navigation built entirely on the public shell/extension contracts.</summary>
public sealed class TabHintsExtension : IExtension
{
    public string Id => "tabs.hints";
    private IShell _shell = null!;
    private ITabBadgeLayer? _badges;
    private IKeyBindingLayer? _defaults;
    private KeyMode? _mode;
    private HintBar? _bar;
    private TabHintSettings _settings = new();
    private IReadOnlyList<TabHint> _hints = [];
    private IReadOnlyList<TabHint>? _captured;
    private string? _defaultsKey;
    private bool _enabled;
    private bool _always;
    private bool _disposed;

    internal bool Picking => _mode?.IsActive == true;
    internal IReadOnlyList<TabHint> Hints => _captured ?? _hints;

    public void Initialize(IShell shell)
    {
        _shell = shell;
        _badges = shell.Badges.CreateLayer(Id);
        _defaults = shell.Keys.CreateDefaultLayer(Id);
        shell.Commands.Register("tab.hints.pick", "Pick tab by hint", "Tabs", Pick, () => _enabled && shell.Tabs.Count > 0,
            "Type the small hint under a tab's status dot; Esc cancels");
        shell.Commands.Register("tab.hints.toggle", "Toggle tab hints", "Tabs", () => { _always = !_always; Render(); }, () => _enabled,
            "Toggle always-visible/on-demand hints for this run; the setting saves your preference");
        shell.Commands.Register("tab.hints.activate", "Activate tab by hint", "Tabs", invocation =>
        {
            var hint = invocation.Arguments["hint"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            var target = _hints.FirstOrDefault(h => string.Equals(h.Text, hint, StringComparison.OrdinalIgnoreCase));
            if (target is null) shell.Ui.Status("No open tab has that hint");
            else Activate(target.TabId);
            return true;
        }, () => _enabled, "A generated direct shortcut resolves the current display-order hint");
        shell.TabOrderChanged += Update;
        shell.SettingsChanged += Reload;
        shell.Keys.MapChanged += Render;
        Reload();
    }

    private void Reload()
    {
        if (_disposed) return;
        _mode?.Dispose();
        _settings = _shell.ExtensionSettings<TabHintSettings>(Id);
        _enabled = _shell.Settings.ExtensionEnabled(Id);
        _always = _settings.AlwaysVisible;
        if (_settings.Display?.ToLowerInvariant() is not ("always" or "ondemand"))
            _shell.Trace("tabs.hints: display must be always or onDemand; using always");
        _defaultsKey = null;
        Update();
    }

    private void Update()
    {
        if (_disposed) return;
        // A pick owns its original IDs, even if groups move or a process closes a tab.
        if (!Picking)
        {
            _hints = TabHints.Assign(_shell.DisplayOrder.Select(t => t.Id).ToArray());
            var modifiers = DirectModifiers();
            var key = $"{_enabled}:{modifiers}:{(modifiers is null ? 0 : _hints.Count)}";
            if (_defaultsKey != key)
            {
                _defaultsKey = key;
                var bindings = new List<Keybinding>();
                if (_enabled)
                {
                    bindings.Add(new("ctrl+shift+q", "tab.hints.pick"));
                    if (modifiers is not null)
                        bindings.AddRange(_hints.Select(h => new Keybinding(
                            modifiers + "+" + string.Join(' ', h.Text.ToCharArray()), "tab.hints.activate",
                            Arguments: new JsonObject { ["hint"] = h.Text })).Where(Available));
                }
                _defaults!.Replace(bindings);
            }
        }
        Render();
    }

    private string? DirectModifiers()
    {
        if (string.IsNullOrWhiteSpace(_settings.DirectModifiers)) return null;
        // Modifiers only, with Ctrl or Alt required: never reserve ordinary typing or
        // rely on Win chords that Windows owns before the application sees them.
        var text = _settings.DirectModifiers.Trim();
        if (KeyChord.TryParse(text + "+a", out var chord) && chord.Key == "A" &&
            (chord.Modifiers & (ChordModifiers.Control | ChordModifiers.Alt)) != 0 && !chord.Modifiers.HasFlag(ChordModifiers.Win)) return text;
        _shell.Trace("tabs.hints: directModifiers must contain Ctrl or Alt and optional Shift, e.g. alt; direct shortcuts disabled");
        return null;
    }

    private bool Available(Keybinding binding)
    {
        if (!KeySequence.TryParse(binding.Keys, out var keys)) return false;
        // Generated shortcuts fill gaps. Choosing Ctrl+Shift must not silently take
        // over views, close-tab, the herd leader or another extension's commands.
        return !_shell.Keys.Map.Bindings.Values.Where(b => !string.Equals(b.Command, "tab.hints.activate", StringComparison.OrdinalIgnoreCase)).Any(b =>
            b.Keys.Equals(keys) || b.Keys.StartsWith(keys.Chords) || keys.StartsWith(b.Keys.Chords));
    }

    private void Pick()
    {
        if (Picking) { _mode!.Dispose(); return; }
        Update();
        _captured = _hints.ToArray();
        // The normal address command already handles floating, detached and dashboard
        // presentations. Its captured target cannot be redirected by a mutable name.
        var bindings = _captured.SelectMany(h => PickKeys(h.Text).Select(keys => new Keybinding(keys, "tab.activate",
            Arguments: new JsonObject { ["target"] = new JsonObject { ["id"] = h.TabId } })));
        _mode = _shell.Keys.EnterMode(bindings, () =>
        {
            _mode = null;
            _captured = null;
            if (_bar is not null) _shell.Ui.HideBar(_bar);
            Update();
        }, TimeSpan.FromMilliseconds(Math.Clamp(_settings.TimeoutMs, 1000, 60000)));
        _mode.PendingChanged += Render;
        _bar ??= new HintBar();
        Render();
        _shell.Ui.ShowBar(_bar);
    }

    private void Activate(string tabId)
    {
        var result = _shell.Presentations.ShowNormally(tabId);
        if (!result.Success) _shell.Ui.Status(result.Error ?? "The tab has closed");
    }

    private static IEnumerable<string> PickKeys(string text)
    {
        IEnumerable<string> keys = [string.Empty];
        foreach (var character in text)
            keys = keys.SelectMany(prefix => char.IsAsciiDigit(character)
                    ? new[] { character.ToString(), "shift+" + character, "numpad" + character }
                    : new[] { character.ToString(), "shift+" + character },
                (prefix, key) => prefix.Length == 0 ? key : prefix + " " + key).ToArray();
        return keys;
    }

    private void Render()
    {
        if (_disposed || _badges is null) return;
        var shown = new Dictionary<string, TabBadge>(StringComparer.OrdinalIgnoreCase);
        var picking = Picking;
        var prefix = picking ? string.Concat(_mode!.Pending.Select(c =>
            c.Key.StartsWith("NumPad", StringComparison.Ordinal) ? c.Key[^1].ToString() :
            c.Key.Length == 2 && c.Key[0] == 'D' ? c.Key[1].ToString() : c.Key)) : string.Empty;
        var pickKeys = _shell.Keys.HintFor("tab.hints.pick");
        var directKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in _shell.Keys.Map.Bindings.Values)
            if (string.Equals(binding.Command, "tab.hints.activate", StringComparison.OrdinalIgnoreCase) && binding.Arguments?["hint"] is JsonValue value && value.TryGetValue<string>(out var text))
                directKeys.TryAdd(text, binding.Keys.ToString());
        var rows = new List<(string Hint, string Label)>();
        if (_enabled && (_always || picking))
            foreach (var hint in Hints)
            {
                if (_shell.Find(hint.TabId) is not { } tab) continue;
                var matches = picking && hint.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                var direct = directKeys.GetValueOrDefault(hint.Text) ?? (hint.Text.Length == 1 && char.IsAsciiDigit(hint.Text[0]) ? _shell.Keys.HintFor("tab.switchTo." + hint.Text) : null);
                var help = $"Tab hint {hint.Text} · {(pickKeys is null ? "Pick tab by hint in the command palette" : pickKeys + ", then " + hint.Text)}" +
                    (direct is null ? string.Empty : $" · {direct}");
                shown[hint.TabId] = new(hint.Text, help, matches);
                if (matches) rows.Add((hint.Text, tab.Label));
            }
        _badges.Replace(shown);
        if (picking) _bar?.Update(prefix, rows);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mode?.Dispose();
        if (_shell is not null)
        {
            _shell.TabOrderChanged -= Update;
            _shell.SettingsChanged -= Reload;
            _shell.Keys.MapChanged -= Render;
            foreach (var command in new[] { "tab.hints.pick", "tab.hints.toggle", "tab.hints.activate" }) _shell.Commands.Remove(command);
            if (_bar is not null) _shell.Ui.HideBar(_bar);
        }
        _defaults?.Dispose();
        _badges?.Dispose();
    }

    private sealed class HintBar : Border
    {
        private readonly TextBlock _caption = new() { Margin = new Thickness(0, 0, 0, 5) };
        private readonly WrapPanel _items = new();

        public HintBar()
        {
            SetResourceReference(BackgroundProperty, "Surface.Chrome");
            SetResourceReference(BorderBrushProperty, "Surface.Border");
            BorderThickness = new Thickness(0, 1, 0, 0);
            Padding = new Thickness(12, 6, 12, 6);
            Focusable = false;
            _caption.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            _caption.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
            _caption.FontSize = 11;
            var panel = new DockPanel();
            DockPanel.SetDock(_caption, Dock.Top);
            panel.Children.Add(_caption);
            panel.Children.Add(new ScrollViewer { Content = _items, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
            Child = panel;
        }

        public void Update(string prefix, IReadOnlyList<(string Hint, string Label)> rows)
        {
            _caption.Text = $"Pick tab by hint{(prefix.Length == 0 ? string.Empty : " · " + prefix + "…")} · Esc cancels";
            _items.Children.Clear();
            foreach (var (hint, label) in rows)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 16, 2) };
                var key = new TextBlock { Text = hint, MinWidth = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 5, 0) };
                key.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
                key.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Base");
                var name = new TextBlock { Text = label, MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis };
                name.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
                name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
                row.Children.Add(key);
                row.Children.Add(name);
                _items.Children.Add(row);
            }
        }
    }
}
