using System.Text.Json.Nodes;

namespace OverShell.Core.Input;

/// <summary>One line of <c>keybindings.jsonc</c>.</summary>
/// <param name="Keys">Chord or sequence text, e.g. <c>ctrl+shift+p</c> or <c>ctrl+shift+k j</c>.</param>
/// <param name="Command">Command id, or <c>unbound</c> / null to remove a default.</param>
/// <param name="Stay">
/// For a sequence: after the command runs, stay in the sequence's prefix so the last chord
/// can be repeated (<c>ctrl+shift+k j j j</c> walks three tabs). Ignored for a single chord.
/// </param>
public sealed record Keybinding(string Keys, string? Command, bool Stay = false);

/// <summary>What a sequence of chords means so far.</summary>
public enum KeyResolution
{
    /// <summary>Nothing is bound at or below these chords.</summary>
    None,

    /// <summary>A command is bound to exactly these chords.</summary>
    Command,

    /// <summary>Longer sequences start with these chords; wait for the next one.</summary>
    Prefix,
}

/// <summary>A command bound to a key sequence.</summary>
public sealed record BoundCommand(KeySequence Keys, string Command, bool Stay);

/// <summary>
/// Keys  command map, layered the way Windows Terminal does it: the embedded defaults
/// first, then what extensions declare as their defaults, then the user's
/// <c>keybindings.jsonc</c>, where a later entry for the same keys wins and
/// <c>"command": "unbound"</c> removes it. Since §12.16 the keys may be a sequence
/// (<c>ctrl+shift+k j</c>): a chord that is both bound and the start of a longer sequence
/// fires at once, which makes the longer one unreachable - that is reported as a problem.
/// </summary>
public sealed class KeybindingMap
{
    private readonly Dictionary<KeySequence, BoundCommand> _bySequence = new();
    private readonly List<string> _problems = [];

    public IReadOnlyList<string> Problems => _problems;

    public IReadOnlyDictionary<KeySequence, BoundCommand> Bindings => _bySequence;

    /// <summary>The single-chord bindings, as before sequences existed.</summary>
    public IEnumerable<KeyValuePair<KeyChord, string>> ChordBindings =>
        _bySequence.Where(kv => kv.Key.Length == 1).Select(kv => new KeyValuePair<KeyChord, string>(kv.Key.First, kv.Value.Command));

    public bool TryResolve(KeyChord chord, out string commandId)
    {
        if (_bySequence.TryGetValue(new KeySequence(chord), out var bound))
        {
            commandId = bound.Command;
            return true;
        }

        commandId = string.Empty;
        return false;
    }

    /// <summary>What the chords pressed so far mean: a command, the start of longer sequences, or nothing.</summary>
    public KeyResolution Resolve(IReadOnlyList<KeyChord> chords, out BoundCommand? bound)
    {
        if (_bySequence.TryGetValue(new KeySequence(chords), out bound))
        {
            return KeyResolution.Command;
        }

        bound = null;
        return _bySequence.Keys.Any(k => k.StartsWith(chords)) ? KeyResolution.Prefix : KeyResolution.None;
    }

    /// <summary>The bindings that continue a prefix, by their next chord - what a hint bar shows.</summary>
    public IReadOnlyList<(KeyChord Next, BoundCommand Binding)> Continuations(IReadOnlyList<KeyChord> prefix) =>
        _bySequence
            .Where(kv => kv.Key.StartsWith(prefix))
            .Select(kv => (kv.Key.Chords[prefix.Count], kv.Value))
            .OrderBy(c => c.Item1.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Every key sequence bound to a command, for the palette's key hints.</summary>
    public IEnumerable<KeySequence> ChordsFor(string commandId) =>
        _bySequence.Where(kv => string.Equals(kv.Value.Command, commandId, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).OrderBy(k => k.Length);

    public string? FirstChordFor(string commandId) =>
        ChordsFor(commandId).Select(c => c.ToString()).FirstOrDefault();

    /// <summary>Applies a list of bindings in order. Later entries override earlier ones.</summary>
    public void Apply(IEnumerable<Keybinding> bindings, string sourceName)
    {
        foreach (var binding in bindings)
        {
            if (!KeySequence.TryParse(binding.Keys, out var sequence))
            {
                _problems.Add($"{sourceName}: cannot parse keys '{binding.Keys}'");
                continue;
            }

            if (string.IsNullOrWhiteSpace(binding.Command) || binding.Command.Equals("unbound", StringComparison.OrdinalIgnoreCase))
            {
                _bySequence.Remove(sequence);
            }
            else
            {
                _bySequence[sequence] = new BoundCommand(sequence, binding.Command, binding.Stay && sequence.Length > 1);
            }
        }
    }

    /// <summary>A bound chord that is also the start of a longer sequence makes the longer one unreachable.</summary>
    private void ReportShadowed()
    {
        foreach (var sequence in _bySequence.Keys.Where(k => k.Length > 1))
        {
            for (var length = 1; length < sequence.Length; length++)
            {
                var prefix = new KeySequence(sequence.Chords.Take(length).ToList());
                if (_bySequence.TryGetValue(prefix, out var shadowing))
                {
                    _problems.Add($"'{sequence}' can never fire: '{prefix}' runs {shadowing.Command} first");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Parses the file shape: either a bare array of <c>{ "keys", "command" }</c> or an
    /// object with a <c>keybindings</c> (or <c>actions</c>) array - both are what Windows
    /// Terminal users have seen. <c>"stay": true</c> is ours.
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

            var stay = entry["stay"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
            list.Add(new Keybinding(keys, command, stay));
        }

        return list;
    }

    /// <summary>
    /// Builds the effective map: the embedded defaults, then what extensions declared
    /// (<paramref name="extensionDefaults"/>, overridable like any default), then the user file.
    /// </summary>
    public static KeybindingMap Load(string? userFilePath, IEnumerable<Keybinding>? extensionDefaults = null)
    {
        var map = new KeybindingMap();

        var defaults = Jsonc.Parse(EmbeddedResources.Read("keybindings.jsonc"), out var defaultsError);
        if (defaultsError is not null)
        {
            map._problems.Add($"defaults: {defaultsError}");
        }

        map.Apply(ParseBindings(defaults, map._problems, "defaults"), "defaults");

        if (extensionDefaults is not null)
        {
            map.Apply(extensionDefaults, "extensions");
        }

        if (userFilePath is not null)
        {
            var user = Jsonc.ReadFile(userFilePath, out var userError);
            if (userError is not null)
            {
                map._problems.Add($"{userFilePath}: {userError}");
            }

            map.Apply(ParseBindings(user, map._problems, "keybindings.jsonc"), "keybindings.jsonc");
        }

        map.ReportShadowed();
        return map;
    }
}
