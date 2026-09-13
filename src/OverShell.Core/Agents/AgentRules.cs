using System.Text.Json.Nodes;

namespace OverShell.Core.Agents;

/// <summary>
/// The rule sets in force: bundled defaults, each replaced by a same-id file from the
/// user's <c>agents/</c> directory when present. Also the place that answers "which
/// harness is this?" from a command line, a title, or a process name.
/// </summary>
public sealed class AgentRules
{
    public const string GenericId = "generic";

    private readonly Dictionary<string, AgentRuleSet> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _problems = [];

    public IReadOnlyCollection<AgentRuleSet> All => _byId.Values;

    public IReadOnlyList<string> Problems => _problems;

    public AgentRuleSet? Find(string? id) => id is null ? null : _byId.GetValueOrDefault(id);

    /// <summary>The fallback for an agent tab whose harness is unknown.</summary>
    public AgentRuleSet Generic => _byId.GetValueOrDefault(GenericId) ?? new AgentRuleSet { Id = GenericId, DisplayName = "Agent" };

    public string? DetectFromCommandline(string? commandline) =>
        string.IsNullOrEmpty(commandline) ? null : Known().FirstOrDefault(r => r.CommandlineRegexes.Any(x => x.IsMatch(commandline)))?.Id;

    public string? DetectFromTitle(string? title) =>
        string.IsNullOrEmpty(title) ? null : Known().FirstOrDefault(r => r.TitleRegexes.Any(x => x.IsMatch(title)))?.Id;

    /// <param name="imageName">Process image name without extension, any case.</param>
    public string? DetectFromProcess(string? imageName) =>
        string.IsNullOrEmpty(imageName)
            ? null
            : Known().FirstOrDefault(r => r.Detect.Process.Any(p => string.Equals(p, imageName, StringComparison.OrdinalIgnoreCase)))?.Id;

    /// <summary>Bundled defaults only. Tests and diagnostics.</summary>
    public static AgentRules LoadDefaults() => Load(null);

    /// <summary>Bundled defaults, then every <c>*.jsonc</c> in <paramref name="userDirectory"/>.</summary>
    public static AgentRules Load(string? userDirectory)
    {
        var rules = new AgentRules();

        foreach (var resource in EmbeddedResources.List("agents"))
        {
            rules.Add(EmbeddedResources.Read(resource), resource, isUser: false);
        }

        if (userDirectory is not null && Directory.Exists(userDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(userDirectory, "*.jsonc").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    rules.Add(File.ReadAllText(file), file, isUser: true);
                }
                catch (IOException e)
                {
                    rules._problems.Add($"{file}: {e.Message}");
                }
            }
        }

        return rules;
    }

    private void Add(string text, string sourceName, bool isUser)
    {
        var node = Jsonc.Parse(text, out var parseError);
        if (node is null)
        {
            _problems.Add($"{sourceName}: {parseError ?? "empty"}");
            return;
        }

        if (node is JsonObject obj && obj["id"] is null)
        {
            // The file name is the id when the file does not say.
            obj["id"] = Path.GetFileNameWithoutExtension(sourceName);
        }

        var set = Jsonc.To<AgentRuleSet>(node, out var shapeError);
        if (set is null)
        {
            _problems.Add($"{sourceName}: {shapeError ?? "not an object"}");
            return;
        }

        if (isUser && _byId.ContainsKey(set.Id))
        {
            // Local overrides always win, whole file (§12.3).
        }

        _byId[set.Id] = set;
    }

    private IEnumerable<AgentRuleSet> Known() => _byId.Values.Where(r => !r.Id.Equals(GenericId, StringComparison.OrdinalIgnoreCase));
}
