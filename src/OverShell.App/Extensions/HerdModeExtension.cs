using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverShell.Core.Extensibility;
using OverShell.Core.Input;

namespace OverShell.App.Extensions;

/// <summary>
/// Herd mode (DESIGN.md §12.16): one leader chord, then single keys for everything you do
/// to the herd - built on the engine's key sequences, as an extension. The navigation
/// keys <em>stay</em> (<c>j j j</c> walks three tabs); actions leave. While the leader is
/// pending a hint bar lists the continuations, generated from the key map, so a user's
/// own sequences appear in it too. Settings: <c>extensions.herd.mode</c>.
/// </summary>
public sealed class HerdModeExtension : IExtension
{
    public const string Leader = "ctrl+shift+k";

    private IShell _shell = null!;
    private HintBar? _bar;
    private HerdModeSettings _settings = new();

    public string Id => "herd.mode";

    public sealed class HerdModeSettings
    {
        /// <summary>Show the hint bar while a sequence is pending.</summary>
        public bool Hints { get; init; } = true;

        /// <summary>Which chord opens the mode (a chord, not a sequence).</summary>
        public string Leader { get; init; } = HerdModeExtension.Leader;
    }

    public void Initialize(IShell shell)
    {
        _shell = shell;
        _settings = shell.ExtensionSettings<HerdModeSettings>(Id);
        var leader = KeyChord.TryParse(_settings.Leader, out _) ? _settings.Leader : Leader;

        // The mode's own commands.
        shell.Commands.Register("tab.last", "Switch to the last used tab", "Tabs", () =>
        {
            if (_lastActive is { } last && shell.Tabs.Contains(last) && !ReferenceEquals(last, shell.ActiveTab))
            {
                shell.Activate(last);
            }
        }, () => _lastActive is not null && shell.Tabs.Count > 1, "Bounces between the two tabs you were last in");

        shell.Commands.Register("herd.hints", "Herd mode: show the keys", "Herd", () => ShowHints(force: true),
            description: $"The keys that follow {KeySequenceText(leader)}");

        // The default sequences. Every one is overridable or removable in keybindings.jsonc.
        shell.Keys.AddDefaults(
        [
            new Keybinding($"{leader} j", "tab.next", Stay: true),
            new Keybinding($"{leader} k", "tab.previous", Stay: true),
            new Keybinding($"{leader} shift+j", "tab.moveRight", Stay: true),
            new Keybinding($"{leader} shift+k", "tab.moveLeft", Stay: true),
            new Keybinding($"{leader} b", "tab.nextBlocked", Stay: true),
            new Keybinding($"{leader} shift+b", "tab.previousBlocked", Stay: true),
            new Keybinding($"{leader} d", "tab.nextDone", Stay: true),
            new Keybinding($"{leader} l", "tab.last"),
            new Keybinding($"{leader} n", "tab.new"),
            new Keybinding($"{leader} x", "tab.close"),
            new Keybinding($"{leader} r", "tab.rename"),
            new Keybinding($"{leader} g", "tab.moveToGroup"),
            new Keybinding($"{leader} e", "tab.explain"),
            new Keybinding($"{leader} p", "prompt.toggle"),
            new Keybinding($"{leader} f", "terminal.find"),
            new Keybinding($"{leader} slash", "palette.tabs"),
            new Keybinding($"{leader} shift+slash", "herd.hints"),
            new Keybinding($"{leader} t", "tab.detach"),
            new Keybinding($"{leader} a", "tab.attach"),
            new Keybinding($"{leader} 1", "tab.switchTo.1"),
            new Keybinding($"{leader} 2", "tab.switchTo.2"),
            new Keybinding($"{leader} 3", "tab.switchTo.3"),
            new Keybinding($"{leader} 4", "tab.switchTo.4"),
            new Keybinding($"{leader} 5", "tab.switchTo.5"),
            new Keybinding($"{leader} 6", "tab.switchTo.6"),
            new Keybinding($"{leader} 7", "tab.switchTo.7"),
            new Keybinding($"{leader} 8", "tab.switchTo.8"),
            new Keybinding($"{leader} 9", "tab.switchTo.9"),
        ]);

        shell.ActiveTabChanged += OnActiveTabChanged;
        shell.Keys.PendingChanged += OnPendingChanged;
        shell.SettingsChanged += () => _settings = shell.ExtensionSettings<HerdModeSettings>(Id);
    }

    private ITab? _previousActive;
    private ITab? _lastActive;

    /// <summary>The tab to bounce back to - the one active before the current one.</summary>
    internal ITab? LastActive => _lastActive;

    private void OnActiveTabChanged(ITab? tab)
    {
        if (ReferenceEquals(tab, _previousActive))
        {
            return;
        }

        _lastActive = _previousActive;
        _previousActive = tab;
    }

    private void OnPendingChanged(IReadOnlyList<KeyChord> pending)
    {
        if (pending.Count == 0)
        {
            Hide();
            return;
        }

        if (_settings.Hints)
        {
            Show(pending);
        }
    }

    private void ShowHints(bool force)
    {
        var pending = _shell.Keys.Pending;
        if (pending.Count > 0)
        {
            Show(pending);
            return;
        }

        if (force && KeyChord.TryParse(_settings.Leader, out var leader))
        {
            // Not pending: show what the leader would offer, for a few seconds.
            Show([leader]);
            _shell.Post(async () =>
            {
                await Task.Delay(4000);
                if (_shell.Keys.Pending.Count == 0)
                {
                    Hide();
                }
            });
        }
    }

    private void Show(IReadOnlyList<KeyChord> pending)
    {
        _bar ??= new HintBar();
        var all = _shell.Keys.Map.Continuations(pending)
            .Select(c => (Key: c.Next.ToString(), Title: TitleOf(c.Binding), c.Binding.Stay, Command: c.Binding.Command))
            .ToList();

        // Digits collapse into one chip; stay keys (navigation) come first, then actions, then the rest.
        var digits = all.Where(c => c.Key.Length == 1 && char.IsAsciiDigit(c.Key[0])).OrderBy(c => c.Key).ToList();
        var others = all.Where(c => !(c.Key.Length == 1 && char.IsAsciiDigit(c.Key[0])));
        var continuations = others
            .OrderByDescending(c => c.Stay)
            .ThenBy(c => c.Key.Contains('+') ? 1 : 0)
            .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Select(c => (c.Key, c.Title, c.Stay))
            .ToList();
        if (digits.Count > 1)
        {
            continuations.Insert(continuations.FindIndex(c => !c.Stay) is var at && at >= 0 ? at : continuations.Count, ($"{digits[0].Key}-{digits[^1].Key}", "Switch to tab", false));
        }
        else if (digits.Count == 1)
        {
            continuations.Add((digits[0].Key, digits[0].Title, digits[0].Stay));
        }

        _bar.Update(string.Join(' ', pending.Select(p => p.ToString())), continuations);
        _shell.Ui.ShowBar(_bar);
    }

    private void Hide()
    {
        if (_bar is not null)
        {
            _shell.Ui.HideBar(_bar);
        }
    }

    private string TitleOf(BoundCommand binding) =>
        _shell.Commands.Find(binding.Command)?.Title ?? binding.Command;

    private static string KeySequenceText(string text) => KeySequence.TryParse(text, out var s) ? s.ToString() : text;

    /// <summary>The bar's state, for the self-test.</summary>
    internal HintBar? Bar => _bar;

    public void Dispose()
    {
        if (_shell is not null)
        {
            _shell.ActiveTabChanged -= OnActiveTabChanged;
            _shell.Keys.PendingChanged -= OnPendingChanged;
        }

        Hide();
    }

    /// <summary>The which-key bar: the pending chords on the left, one chip per continuation.</summary>
    public sealed class HintBar : Border
    {
        private readonly TextBlock _prefix;
        private readonly WrapPanel _chips;

        public HintBar()
        {
            SetResourceReference(BackgroundProperty, "Surface.Chrome");
            SetResourceReference(BorderBrushProperty, "Surface.Border");
            BorderThickness = new Thickness(0, 1, 0, 0);
            Padding = new Thickness(10, 6, 10, 6);
            Focusable = false;

            _prefix = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), FontWeight = FontWeights.SemiBold };
            _prefix.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Base");
            _prefix.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
            _prefix.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Small");

            _chips = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_prefix, Dock.Left);
            row.Children.Add(_prefix);
            row.Children.Add(_chips);
            Child = row;
        }

        public string Prefix => _prefix.Text;

        public List<string> Keys { get; private set; } = [];

        public void Update(string prefix, IReadOnlyList<(string Key, string Title, bool Stay)> continuations)
        {
            _prefix.Text = prefix;
            Keys = continuations.Select(c => c.Key).ToList();
            _chips.Children.Clear();
            foreach (var (key, title, stay) in continuations)
            {
                var keyBox = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                keyBox.SetResourceReference(BackgroundProperty, "Surface.Raised");
                keyBox.SetResourceReference(BorderBrushProperty, "Surface.Border");
                keyBox.BorderThickness = new Thickness(1);
                var keyText = new TextBlock { Text = key, FontWeight = FontWeights.SemiBold };
                keyText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
                keyText.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
                keyText.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Detail");
                keyBox.Child = keyText;

                var label = new TextBlock { Text = stay ? title + " \u21BB" : title, VerticalAlignment = VerticalAlignment.Center, ToolTip = stay ? "\u21BB: repeats - the mode stays on" : "Leaves the mode" };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
                label.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
                label.SetResourceReference(TextBlock.FontSizeProperty, "Font.Size.Small");

                var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 16, 2) };
                chip.Children.Add(keyBox);
                chip.Children.Add(label);
                _chips.Children.Add(chip);
            }
        }
    }
}
