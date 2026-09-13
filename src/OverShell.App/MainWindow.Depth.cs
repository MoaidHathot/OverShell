using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OverShell.App.Chrome;
using OverShell.Core;
using OverShell.Core.Agents;
using OverShell.Core.Settings;

namespace OverShell.App;

/// <summary>
/// P2 depth (DESIGN.md §12.11): the prompt bar and snippets, tab groups and drag reorder,
/// the explain panel, skins.
/// </summary>
public partial class MainWindow
{
    private IReadOnlyList<Snippet> _snippets = [];
    private readonly List<string> _snippetCommands = [];
    private ExplainWindow? _explain;

    internal PromptBar PromptBarView => Prompt;

    internal IReadOnlyList<Snippet> LoadedSnippets => _snippets;

    private void InitializeDepth()
    {
        Prompt.SendRequested += (text, target) => SendPrompt(text, target);
        Prompt.HideRequested += () => ShowPromptBar(false);

        _tabStrip.TabMoveRequested += MoveTab;

        _commands.Register("prompt.toggle", "Prompt bar: show / hide", "Prompt", () => ShowPromptBar(Prompt.Visibility != Visibility.Visible), description: "Type once, send to the active tab or every agent");
        _commands.Register("prompt.broadcast", "Prompt bar: broadcast to all agents", "Prompt", () =>
        {
            Prompt.SelectedTarget = PromptTarget.Agents;
            ShowPromptBar(true);
        });
        _commands.Register("prompt.blocked", "Prompt bar: answer the agents needing you", "Prompt", () =>
        {
            Prompt.SelectedTarget = PromptTarget.Blocked;
            ShowPromptBar(true);
        }, () => Tabs.Any(t => t.State is AgentState.Blocked));

        _commands.Register("tab.moveToGroup", "Move tab to group…", "Tabs", MoveActiveTabToGroup, () => ActiveTab is not null, "Empty name removes the tab from its group");
        _commands.Register("tab.moveLeft", "Move tab left / up", "Tabs", () => { if (ActiveTab is { } t) MoveTab(t, Tabs.IndexOf(t) - 1); }, () => ActiveTab is { } t && Tabs.IndexOf(t) > 0);
        _commands.Register("tab.moveRight", "Move tab right / down", "Tabs", () => { if (ActiveTab is { } t) MoveTab(t, Tabs.IndexOf(t) + 1); }, () => ActiveTab is { } t && Tabs.IndexOf(t) < Tabs.Count - 1);

        LoadSnippets();
    }

    // ------------------------------------------------------------- prompt bar

    private void ShowPromptBar(bool show)
    {
        Prompt.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            Prompt.SetSnippets(_snippets);
            Dispatcher.BeginInvoke(Prompt.FocusInput, DispatcherPriority.Input);
        }
        else
        {
            ActiveTab?.Surface.Focus();
        }
    }

    /// <summary>Tabs a prompt target resolves to right now.</summary>
    internal IReadOnlyList<TerminalTab> ResolvePromptTargets(PromptTarget target) => target switch
    {
        PromptTarget.Agents => Tabs.Where(t => t.IsAgent && t.IsRunning).ToList(),
        PromptTarget.Blocked => Tabs.Where(t => t.State is AgentState.Blocked && t.IsRunning).ToList(),
        PromptTarget.All => Tabs.Where(t => t.IsRunning).ToList(),
        _ => ActiveTab is { } active ? [active] : [],
    };

    /// <summary>
    /// Types <paramref name="text"/> into every target and presses Enter. Multi-line text
    /// goes through <see cref="TerminalTab.Paste"/>, so it arrives bracketed where the
    /// application asked for that and as one block everywhere else.
    /// </summary>
    internal int SendPrompt(string text, PromptTarget target)
    {
        var targets = ResolvePromptTargets(target);
        if (targets.Count == 0)
        {
            ShowStatusMessage(target == PromptTarget.Active ? "No active tab" : "No tab matches that target");
            return 0;
        }

        foreach (var tab in targets)
        {
            tab.Paste(text);
            tab.SendText("\r");
        }

        _trace.Write($"prompt -> {target} ({targets.Count} tab(s)): {text.ReplaceLineEndings(" ")}");
        if (targets.Count > 1)
        {
            ShowStatusMessage($"Sent to {targets.Count} tabs");
        }

        return targets.Count;
    }

    private void LoadSnippets()
    {
        foreach (var id in _snippetCommands)
        {
            _commands.Remove(id);
        }

        _snippetCommands.Clear();

        var problems = new List<string>();
        _snippets = Snippets.Load(AppPaths.SnippetsFile, problems);
        foreach (var problem in problems)
        {
            _trace.Write($"snippets: {problem}");
        }

        foreach (var snippet in _snippets)
        {
            var id = Snippets.CommandId(snippet.Name);
            if (_commands.Find(id) is not null)
            {
                _trace.Write($"snippets: '{snippet.Name}' collides with command {id}; skipped");
                continue;
            }

            var captured = snippet;
            _commands.Register(id, $"Snippet: {snippet.Name}", "Snippets", () => SendPrompt(captured.Text, PromptTarget.Active), description: snippet.Description ?? Truncate(snippet.Text));
            _snippetCommands.Add(id);
        }

        Prompt.SetSnippets(_snippets);
    }

    private static string Truncate(string s)
    {
        var line = s.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : line[..77] + "…";
    }

    // ------------------------------------------------------------- groups & order

    /// <summary>Moves a tab to <paramref name="index"/> in the collection, adopting the group of the tab it displaces.</summary>
    private void MoveTab(TerminalTab tab, int index)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, Tabs.Count - 1);
        if (index == from)
        {
            return;
        }

        var displaced = Tabs[index];
        if (displaced.Group != tab.Group)
        {
            // Crossing a group boundary means joining that group; the view regroups live.
            tab.Group = displaced.Group;
        }

        Tabs.Move(from, index);
        _trace.Write($"[{tab.Id}] moved {from} -> {index}{(tab.Group is null ? string.Empty : $" (group '{tab.Group}')")}");
    }

    private void MoveActiveTabToGroup()
    {
        if (ActiveTab is not { } tab)
        {
            return;
        }

        _palette?.Close();
        _palette = PaletteWindow.Prompt(this, "Group name — empty removes the tab from its group", tab.Group ?? string.Empty, name => SetGroup(tab, name));
        _palette.Closed += (_, _) =>
        {
            _palette = null;
            ActiveTab?.Surface.Focus();
        };
    }

    /// <summary>Sets the group and moves the tab next to that group's last member, so source order stays the visible order.</summary>
    internal void SetGroup(TerminalTab tab, string? name)
    {
        var group = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        tab.Group = group;

        var others = Tabs.Where(t => !ReferenceEquals(t, tab) && string.Equals(t.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0)
        {
            var from = Tabs.IndexOf(tab);
            var last = Tabs.IndexOf(others[^1]);
            var target = from < last ? last : last + 1;
            target = Math.Clamp(target, 0, Tabs.Count - 1);
            if (target != from)
            {
                Tabs.Move(from, target);
            }
        }

        _trace.Write($"[{tab.Id}] group -> {group ?? "(none)"}");
    }

    // ------------------------------------------------------------- explain

    private void OpenExplain()
    {
        if (TargetTab is not { } tab)
        {
            return;
        }

        if (_explain is { IsVisible: true })
        {
            _explain.Close();
            return;
        }

        _explain = new ExplainWindow(this, tab);
        _explain.Closed += (_, _) =>
        {
            _explain = null;
            ActiveTab?.Surface.Focus();
        };
        _explain.Show();
    }

    internal ExplainWindow? Explain => _explain;

    // ------------------------------------------------------------- skins

    private void ApplySkinFromSettings()
    {
        var problem = SkinLoader.Apply(_settings.Skin);
        if (problem is not null)
        {
            _trace.Write($"skin: {problem}");
            ShowStatusMessage(problem);
        }
        else if (_settings.Skin is not null)
        {
            _trace.Write($"skin: applied '{_settings.Skin}'");
        }
    }

    /// <summary>True when keyboard focus sits in a WPF text box (prompt bar, palette): editing chords belong to it.</summary>
    private static bool TextInputHasFocus() => Keyboard.FocusedElement is TextBoxBase;
}
