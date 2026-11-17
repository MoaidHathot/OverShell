using System.Text.Json.Nodes;

namespace OverShell.Core.Extensibility;

/// <summary>
/// Version 1 of the command-extension feed. A complete, replaceable snapshot, with no
/// screen reads or output/activity timestamps: terminal output alone does not publish.
/// </summary>
public static class ExtensionSnapshot
{
    public static bool IncludesProperty(string? property) => string.IsNullOrEmpty(property) || property is
        nameof(ITab.Label) or nameof(ITab.Title) or nameof(ITab.Alias) or nameof(ITab.Badge) or
        nameof(ITab.Harness) or nameof(ITab.IsAgent) or nameof(ITab.State) or nameof(ITab.Unread) or
        nameof(ITab.NeedsAttention) or nameof(ITab.AttentionSince) or nameof(ITab.WorkingDirectory) or
        nameof(ITab.Project) or nameof(ITab.Group) or nameof(ITab.IsRunning) or nameof(ITab.IsActive) or
        nameof(ITab.IsFocused) or nameof(ITab.Detached) or nameof(ITab.Floating) or
        nameof(ITab.SessionId) or nameof(ITab.SessionTitle) or nameof(ITab.OpenRequest);

    public static JsonObject Create(IShell shell, string extensionId)
    {
        var tabs = new JsonArray();
        foreach (var tab in shell.DisplayOrder)
        {
            tabs.Add(new JsonObject
            {
                ["id"] = tab.Id,
                ["label"] = tab.Label,
                ["title"] = tab.Title,
                ["alias"] = tab.Alias,
                ["hint"] = tab.Badge?.Text,
                ["harness"] = tab.Harness,
                ["isAgent"] = tab.IsAgent,
                ["state"] = tab.State.ToString(),
                ["unread"] = tab.Unread,
                ["needsAttention"] = tab.NeedsAttention,
                ["attentionSince"] = tab.AttentionSince?.ToString("o"),
                ["cwd"] = tab.WorkingDirectory,
                ["project"] = tab.Project,
                ["group"] = tab.Group,
                ["isRunning"] = tab.IsRunning,
                ["isActive"] = tab.IsActive,
                ["isFocused"] = tab.IsFocused,
                ["detached"] = tab.Detached,
                ["floating"] = tab.Floating,
                ["sessionId"] = tab.SessionId,
                ["sessionTitle"] = tab.SessionTitle,
                ["request"] = tab.OpenRequest is { } request ? new JsonObject
                {
                    ["id"] = request.Id,
                    ["kind"] = request.Kind,
                    ["title"] = request.Title,
                } : null,
            });
        }

        return new JsonObject
        {
            ["type"] = "snapshot",
            ["protocol"] = 1,
            ["extensionId"] = extensionId,
            ["hostPid"] = Environment.ProcessId,
            ["settings"] = shell.Settings.Extensions.GetValueOrDefault(extensionId)?.DeepClone() ?? new JsonObject(),
            ["tabs"] = tabs,
        };
    }
}
