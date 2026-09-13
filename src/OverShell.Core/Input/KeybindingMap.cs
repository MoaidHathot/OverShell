using System.Text.Json.Nodes;

namespace OverShell.Core.Input;

/// <summary>One line of <c>keybindings.jsonc</c>.</summary>
/// <param name="Keys">Chord text, e.g. <c>ctrl+shift+p</c>.</param>
/// <param name="Command">Command id, or <c>unbound</c> / null to remove a default.</param>
public sealed record Keybinding(string Keys, string? Command);

/// <summary>
/// Chord → command map, layered the way Windows Terminal does it: the embedded defaults
/// first, then the user's <c>keybindings.jsonc</c>, where a later entry for the same
/// chord wins and <c>"command": "unbound"</c> removes it.
/// </summary>
public sealed class KeybindingMap
{
    private readonly Dictionary<KeyChord, string> _byChord = new();
    private readonly List<string> _problems = [];

    public IReadOnlyList<string> Problems => _problems;

    public IReadOnlyDictionary<KeyChord, string> Bindings => _byChord;

    public bool TryResolve(KeyChord chord, out string commandId) =>
        _byChord.TryGetValue(chord, out commandId!);

    /// <summary>Every chord bound to a command, for the palette's key hints.</summary>
    public IEnumerable<KeyChord> ChordsFor(string commandId) =>
        _byChord.Where(kv => string.Equals(kv.Value, commandId, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key);

    public string? FirstChordFor(string commandId) =>
        ChordsFor(commandId).Select(c => c.ToString()).FirstOrDefault();

    /// <summary>Applies a list of bindings in order. Later entries override earlier ones.</summary>
    public void Apply(IEnumerable<Keybinding> bindings, string sourceName)
    {
        foreach (var binding in bindings)
        {
            if (!KeyChord.TryParse(binding.Keys, out var chord))
            {
                _problems.Add($"{sourceName}: cannot parse keys '{binding.Keys}'");
                continue;
            }

            if (string.IsNullOrWhiteSpace(binding.Command) || binding.Command.Equals("unbound", StringComparison.OrdinalIgnoreCase))
            {
                _byChord.Remove(chord);
            }
            else
            {
                _byChord[chord] = binding.Command;
            }
        }
    }

    /// <summary>
    /// Parses the file shape: either a bare array of <c>{ "keys", "command" }</c> or an
    /// object with a <c>keybindings</c> (or <c>actions</c>) array — both are what Windows
    /// Terminal users have seen.
    /// </summary>
    public static IReadOnlyList<Keybinding> ParseBindings(JsonNode? root, List<string> problems, string sourceName)
    {
        var list = new List<Keybinding>();
        var array = root switch
        {
            JsonArray a => a,
            JsonObject o when o["keybindings"] is JsonArray kb => kb,
            JsonObject o when o["actions"] is JsonArray ac => ac,
            null => null,
            _ => null,
        };

        if (array is null)
        {
            if (root is not null)
            {
                problems.Add($"{sourceName}: expected an array of bindings or an object with \"keybindings\"");
            }

            return list;
        }

        foreach (var item in array)
        {
            if (item is not JsonObject entry)
            {
                problems.Add($"{sourceName}: binding entries must be objects");
                continue;
            }

            var keys = entry["keys"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(keys))
            {
                problems.Add($"{sourceName}: binding without \"keys\"");
                continue;
            }

            var command = entry["command"] switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonObject o => o["action"]?.GetValue<string>(),
                _ => null,
            };

            list.Add(new Keybinding(keys, command));
        }

        return list;
    }

    /// <summary>Builds the effective map from the embedded defaults plus an optional user file.</summary>
    public static KeybindingMap Load(string? userFilePath)
    {
        var map = new KeybindingMap();

        var defaults = Jsonc.Parse(EmbeddedResources.Read("keybindings.jsonc"), out var defaultsError);
        if (defaultsError is not null)
        {
            map._problems.Add($"defaults: {defaultsError}");
        }

        map.Apply(ParseBindings(defaults, map._problems, "defaults"), "defaults");

        if (userFilePath is not null)
        {
            var user = Jsonc.ReadFile(userFilePath, out var userError);
            if (userError is not null)
            {
                map._problems.Add($"{userFilePath}: {userError}");
            }

            map.Apply(ParseBindings(user, map._problems, "keybindings.jsonc"), "keybindings.jsonc");
        }

        return map;
    }
}
