using System.Text.Json.Nodes;
using OverShell.Core.Commands;
using OverShell.Core.Extensibility;
using OverShell.Core.Integrations;
using OverShell.Core.Tabs;

namespace OverShell.App;

public partial class MainWindow
{
    private void InitializeTabCreation()
    {
        _commands.Register("tab.newFloating", "New floating tab", "Tabs", invocation => { OpenFloatingFromCommand(invocation, scratch: false, oneShot: false); return true; },
            description: "A normal restorable tab, initially floating in the invoking directory");
        _commands.Register("tab.scratch.new", "New scratch floating terminal", "Tabs", invocation => { OpenFloatingFromCommand(invocation, scratch: true, oneShot: false); return true; },
            description: "Temporary shell; Hide keeps it alive, Terminate ends it, Keep makes it restorable");
        _commands.Register("tab.scratch.command", "Run a command in a scratch float…", "Tabs", invocation => { OpenFloatingFromCommand(invocation, scratch: true, oneShot: true); return true; },
            description: "Run once through the configured command shell; exit policy follows the real process");
        _commands.Register("tab.keep", "Keep this tab", "Tabs", invocation =>
        {
            if (ResolveTarget(invocation) is { } tab)
            {
                var result = SetTabLifetime(tab.Id, TabLifetime.Standard);
                ShowStatusMessage(result.Success ? $"Keeping {tab.Label}: restoration and history enabled; output retained on exit" : result.Error!);
            }
            return true;
        });
    }

    private async void OpenFloatingFromCommand(CommandInvocation invocation, bool scratch, bool oneShot)
    {
        var body = invocation.Arguments.DeepClone().AsObject();
        body["presentation"] = "floating";
        body["scratch"] = scratch;
        var source = invocation.Context.TabId is { } id ? _tabsById.GetValueOrDefault(id) : TargetTab;
        var origin = Navigation.Capture();
        if (!body.ContainsKey("cwd") && (!scratch || _settings.Scratch.InheritDirectory)) body["cwd"] = source?.WorkingDirectory;
        if (oneShot && body["process"] is null)
        {
            var command = ShellControl.Str(body, "command") ?? await _shell.Ui.PromptAsync($"Command to run once ({_settings.Scratch.CommandShell})");
            if (string.IsNullOrWhiteSpace(command)) return;
            body.Remove("command");
            body["process"] = new JsonObject
            {
                ["executable"] = _settings.Scratch.CommandShell,
                ["arguments"] = new JsonArray(_settings.Scratch.CommandArguments.Append(command).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            };
        }
        var request = TabRequestParser.Parse(body, out var error, defaultActivate: true);
        if (request is null) { ShowStatusMessage(error!); return; }
        OpenTabFor(request, out _, origin);
    }

    internal TabActionResult SetTabLifetime(string tabId, TabLifetime lifetime)
    {
        if (!_tabsById.TryGetValue(tabId, out var tab)) return TabActionResult.Fail("The tab has closed");
        if (!Enum.IsDefined(lifetime.OnExit)) return TabActionResult.Fail("Unknown exit policy");
        tab.SetLifetime(lifetime);
        SaveSession(force: true);
        return TabActionResult.Ok;
    }
}
