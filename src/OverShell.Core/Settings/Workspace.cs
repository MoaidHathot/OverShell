using System.Text.Json.Nodes;

namespace OverShell.Core.Settings;

/// <summary>One tab a workspace opens.</summary>
public sealed class WorkspaceTab
{
    /// <summary>Profile name or id; the default profile when absent or unknown.</summary>
    public string? Profile { get; init; }

    /// <summary>Starting directory; the profile's own when absent or missing on disk. Environment variables expand.</summary>
    public string? Cwd { get; init; }

    public string? Label { get; init; }

    public string? Group { get; init; }

    /// <summary>
    /// A command to run once the shell is at its prompt (<c>opencode</c>, <c>npm run dev</c>);
    /// for a profile whose program is the agent the command's arguments are appended instead.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>Open in a tear-off window of its own.</summary>
    public bool Detached { get; init; }
}

/// <summary>
/// A named set of tabs to open together (DESIGN.md §12.14): <c>workspaces\&lt;name&gt;.jsonc</c>
/// in the configuration root, so it travels with the dotfiles. History is what happened;
/// a workspace is what you want — "OverShell dev: a shell in the repo, OpenCode in the
/// repo, the tests".
/// </summary>
public sealed class Workspace
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>A view to switch to when the workspace opens; null leaves the view alone.</summary>
    public string? View { get; init; }

    public List<WorkspaceTab> Tabs { get; init; } = [];

    /// <summary>Where it was loaded from, or null for one built in memory.</summary>
    public string? Path { get; init; }

    /// <summary>The command id that opens it: <c>workspace.open.&lt;slug&gt;</c>.</summary>
    public string CommandId => "workspace.open." + Slug(Name);

    /// <summary>A file-name-safe, command-id-safe form of a name: letters, digits, dash; lower-case.</summary>
    public static string Slug(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        var dash = false;
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                dash = false;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }
        }

        var slug = sb.ToString().TrimEnd('-');
        return slug.Length == 0 ? "workspace" : slug;
    }

    public JsonObject ToJson()
    {
        var tabs = new JsonArray();
        foreach (var tab in Tabs)
        {
            var o = new JsonObject();
            if (tab.Profile is not null) { o["profile"] = tab.Profile; }
            if (tab.Cwd is not null) { o["cwd"] = tab.Cwd; }
            if (tab.Label is not null) { o["label"] = tab.Label; }
            if (tab.Group is not null) { o["group"] = tab.Group; }
            if (tab.Command is not null) { o["command"] = tab.Command; }
            if (tab.Detached) { o["detached"] = true; }
            tabs.Add(o);
        }

        var root = new JsonObject { ["name"] = Name };
        if (Description is not null) { root["description"] = Description; }
        if (View is not null) { root["view"] = View; }
        root["tabs"] = tabs;
        return root;
    }
}

/// <summary>The workspaces in the configuration root, re-read on reload like layouts.</summary>
public sealed class WorkspaceCatalog
{
    private readonly Dictionary<string, Workspace> _bySlug = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _problems = [];

    public IReadOnlyCollection<Workspace> All => _bySlug.Values;

    public IReadOnlyList<string> Problems => _problems;

    public Workspace? Find(string? nameOrSlug) =>
        nameOrSlug is null ? null : _bySlug.GetValueOrDefault(Workspace.Slug(nameOrSlug));

    public static WorkspaceCatalog Load(string? directory)
    {
        var catalog = new WorkspaceCatalog();
        if (directory is null || !Directory.Exists(directory))
        {
            return catalog;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonc").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                catalog.Add(File.ReadAllText(file), file);
            }
            catch (IOException e)
            {
                catalog._problems.Add($"{file}: {e.Message}");
            }
        }

        return catalog;
    }

    private void Add(string text, string file)
    {
        var node = Jsonc.Parse(text, out var parseError);
        if (node is not JsonObject obj)
        {
            _problems.Add($"{file}: {parseError ?? "not an object"}");
            return;
        }

        if (obj["name"] is null)
        {
            obj["name"] = Path.GetFileNameWithoutExtension(file);
        }

        var workspace = Jsonc.To<Workspace>(obj, out var shapeError);
        if (workspace is null)
        {
            _problems.Add($"{file}: {shapeError ?? "not a workspace"}");
            return;
        }

        if (workspace.Tabs.Count == 0)
        {
            _problems.Add($"{file}: no tabs");
            return;
        }

        _bySlug[Workspace.Slug(workspace.Name)] = new Workspace
        {
            Name = workspace.Name,
            Description = workspace.Description,
            View = workspace.View,
            Tabs = workspace.Tabs,
            Path = file,
        };
    }

    /// <summary>Writes <paramref name="workspace"/> as <c>&lt;slug&gt;.jsonc</c> under <paramref name="directory"/>, with a header; returns the path.</summary>
    public static string Save(string directory, Workspace workspace)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Workspace.Slug(workspace.Name) + ".jsonc");
        var header = "// OverShell workspace - `workspace.open." + Workspace.Slug(workspace.Name) + "` opens these tabs next to the open ones.\n"
                   + "// profile: a Windows Terminal profile name or guid (default profile when absent). cwd: starting directory.\n"
                   + "// command: typed once the shell is at its prompt; for a profile whose program is the agent its arguments are appended.\n";
        File.WriteAllText(path, header + workspace.ToJson().ToJsonString(Jsonc.SerializerOptions) + "\n", new System.Text.UTF8Encoding(false));
        return path;
    }
}
