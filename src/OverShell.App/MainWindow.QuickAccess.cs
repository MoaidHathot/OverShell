using System.Text.Json.Nodes;
using OverShell.Core.Commands;
using OverShell.Core.Extensibility;
using OverShell.Core.Input;
using OverShell.Core.Tabs;

namespace OverShell.App;

public partial class MainWindow
{
    private TabAddressing _addresses = null!;
    private readonly RuntimeBindings _runtimeShortcuts = new();
    private readonly Dictionary<Guid, int> _runtimeHotKeys = [];
    internal ITabAddressing Addresses => _addresses;
    internal IReadOnlyList<RuntimeBinding> RuntimeShortcuts => _runtimeShortcuts.Items;

    private void InitializeQuickAccess()
    {
        _addresses = new TabAddressing(() => Tabs.Select(t => new TabIdentity(t.Id, t.Label)).ToArray());
        _addresses.Changed += change =>
        {
            if (_tabsById.TryGetValue(change.TabId, out var tab)) tab.SetAlias(change.Alias);
        };
        _commands.Dispatch = (invocation, run) =>
        {
            var explicitTarget = invocation.Arguments.ContainsKey("target");
            var tab = explicitTarget ? ResolveTarget(invocation)
                : invocation.Context.TabId is { } id ? _tabsById.GetValueOrDefault(id) : null;
            if (tab is null && (explicitTarget || invocation.Context.TabId is not null))
            {
                if (!explicitTarget) ShowStatusMessage("The target tab has closed");
                return true;
            }
            return ForCommandTarget(tab, run);
        };
        _commands.Register("tab.activate", "Activate tab by address", "Tabs", invocation =>
        {
            if (ResolveTarget(invocation) is { } tab)
            {
                ActiveTab = tab;
                if (!tab.Detached) { if (DashboardVisible) ApplyView("terminal"); BringToFront(); }
            }
            return true;
        });
        _commands.Register("tab.alias", "Set tab alias…", "Tabs", invocation =>
        {
            if (ResolveTarget(invocation) is not { } tab) return true;
            if (invocation.Arguments.ContainsKey("alias"))
            {
                if (invocation.Arguments["alias"] is not null && !(invocation.Arguments["alias"] is JsonValue value && value.TryGetValue<string>(out _)))
                    ShowStatusMessage("Alias must be text or null");
                else ShowAliasResult(tab, _addresses.Assign(tab.Id, invocation.Arguments["alias"]?.GetValue<string>()));
            }
            else EditAlias(tab);
            return true;
        }, description: "A short unique address, kept only while this tab is open; empty clears it");
        _commands.Register("tab.shortcut.assign", "Assign tab shortcut…", "Tabs", invocation =>
        {
            if (ResolveTarget(invocation) is { } tab) EditShortcut(tab);
            return true;
        }, description: "An application chord, leader sequence or global hotkey for this live tab");
        _commands.Register("tab.shortcut.remove", "Remove tab shortcuts", "Tabs", invocation =>
        {
            if (ResolveTarget(invocation) is { } tab)
                foreach (var item in RuntimeShortcuts.Where(i => i.TabId == tab.Id).ToArray()) RemoveRuntimeShortcut(item.Id);
            return true;
        });
    }

    internal TerminalTab? ResolveTarget(CommandInvocation invocation)
    {
        TabSelector? selector;
        if (invocation.Arguments.ContainsKey("target"))
        {
            selector = TabSelector.Parse(invocation.Arguments["target"]);
            if (selector is null) { ShowStatusMessage("Target must contain exactly one id, alias or label"); return null; }
        }
        else selector = new TabSelector(Id: invocation.Context.TabId ?? TargetTab?.Id);
        var result = _addresses.Resolve(selector);
        if (result.TabId is { } id && _tabsById.TryGetValue(id, out var tab)) return tab;
        ShowStatusMessage(result.Error ?? "The target tab has closed");
        return null;
    }

    internal bool ExecuteForTab(string command, TerminalTab tab, JsonObject? arguments = null) =>
        ForCommandTarget(tab, () => _commands.TryExecute(command, arguments, new CommandContext(tab.Id, tab.Detached ? tab.Id : null)));

    private async void EditAlias(TerminalTab tab)
    {
        var alias = await _shell.Ui.PromptAsync("Tab alias — empty clears it (this open tab only)", tab.Alias ?? string.Empty);
        if (alias is not null) ShowAliasResult(tab, _addresses.Assign(tab.Id, alias));
    }

    private void ShowAliasResult(TerminalTab tab, TabActionResult result) =>
        ShowStatusMessage(result.Success ? tab.Alias is { } alias ? $"@{alias} → {tab.Label}" : "Tab alias cleared" : result.Error!);

    private async void EditShortcut(TerminalTab tab)
    {
        var actions = new List<PickItem> { new("Activate tab", Tag: "tab.activate") };
        if (_commands.Find("tab.float.toggle") is not null) actions.Add(new("Toggle floating", Tag: "tab.float.toggle"));
        var action = await _shell.Ui.PickAsync("⌨", "Choose a shortcut action", actions);
        if (action?.Tag is not string command) return;
        var scope = await _shell.Ui.PickAsync("⌨", "Choose shortcut scope", [new("In OverShell", Tag: false), new("Global hotkey", "Available from other applications", Tag: true)]);
        if (scope?.Tag is not bool global) return;
        var keys = await _shell.Ui.PromptAsync("Shortcut — e.g. ctrl+alt+b or ctrl+alt+k b");
        if (string.IsNullOrWhiteSpace(keys)) return;
        var result = AssignRuntimeShortcut(tab.Id, new Keybinding(keys, command, Arguments: new JsonObject { ["target"] = new JsonObject { ["id"] = tab.Id } }), global);
        ShowStatusMessage(result.Error ?? $"{keys} → {tab.DisplayLabel} (until this tab closes)");
    }

    internal RuntimeBindingResult AssignRuntimeShortcut(string tabId, Keybinding binding, bool global)
    {
        if (!_tabsById.ContainsKey(tabId)) return new(null, "The tab has closed");
        if (binding.Command is null || _commands.Find(binding.Command) is null) return new(null, "Unknown command");
        // The registration is owned by and addresses this ID even after aliases or labels change.
        var args = binding.Arguments?.DeepClone().AsObject() ?? new JsonObject();
        args["target"] = new JsonObject { ["id"] = tabId };
        binding = binding with { Arguments = args };
        var result = _runtimeShortcuts.Add(tabId, binding, _keybindings, global);
        if (result.Registration is not { } registration) return result;
        if (global)
        {
            KeyChord.TryParse(binding.Keys, out var chord);
            var hotkey = _shell.Ui.RegisterGlobalHotKey(chord, () =>
            {
                if (_tabsById.TryGetValue(tabId, out var target)) ExecuteForTab(binding.Command, target, binding.Arguments);
            });
            if (hotkey.Handle is not { } handle)
            {
                _runtimeShortcuts.Remove(registration.Id);
                return new(null, hotkey.Error);
            }
            _runtimeHotKeys[registration.Id] = handle;
        }
        RefreshRuntimeShortcuts();
        return result;
    }

    internal bool RemoveRuntimeShortcut(Guid id)
    {
        if (!_runtimeShortcuts.Remove(id)) return false;
        if (_runtimeHotKeys.Remove(id, out var handle)) _shell.Ui.UnregisterGlobalHotKey(handle);
        RefreshRuntimeShortcuts();
        return true;
    }

    private void RefreshRuntimeShortcuts()
    {
        LoadKeybindings();
        foreach (var tab in Tabs)
            tab.SetShortcutHint(string.Join(" · ", RuntimeShortcuts.Where(i => i.TabId == tab.Id).Select(i => i.Binding.Keys + (i.Global ? " (global)" : ""))));
    }

    private void ForgetQuickAccess(TerminalTab tab)
    {
        _floatingPrompts.Remove(tab.Id);
        _addresses.Forget(tab.Id);
        foreach (var item in RuntimeShortcuts.Where(i => i.TabId == tab.Id).ToArray()) RemoveRuntimeShortcut(item.Id);
    }
}
