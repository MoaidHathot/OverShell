using System.Text.Json.Nodes;

namespace OverShell.Core.Settings;

/// <summary>
/// Machine-local state OverShell remembers between runs (<c>%LOCALAPPDATA%\OverShell\state.json</c>):
/// today the labels users gave their tabs, keyed by profile and working directory —
/// the only stable identity a tab has until sessions themselves persist (P2).
/// Writes are whole-file and tolerant: a corrupt file is replaced, never thrown over.
/// </summary>
public sealed class PersistedState
{
    private readonly string _path;
    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

    private PersistedState(string path)
    {
        _path = path;
    }

    public IReadOnlyDictionary<string, string> Labels => _labels;

    public static PersistedState Load(string path)
    {
        var state = new PersistedState(path);
        if (Jsonc.ReadFile(path, out _) is JsonObject root && root["labels"] is JsonObject labels)
        {
            foreach (var (key, value) in labels)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var label) && !string.IsNullOrWhiteSpace(label))
                {
                    state._labels[key] = label;
                }
            }
        }

        return state;
    }

    /// <summary>The key a tab is remembered under: profile id plus the directory it was labelled in.</summary>
    public static string LabelKey(string? profileId, string? workingDirectory) =>
        $"{profileId ?? string.Empty}|{(workingDirectory ?? string.Empty).TrimEnd('\\', '/')}";

    public string? LabelFor(string? profileId, string? workingDirectory) =>
        _labels.GetValueOrDefault(LabelKey(profileId, workingDirectory));

    /// <summary>Remembers (or, with an empty label, forgets) and saves. Returns false when the file could not be written.</summary>
    public bool SetLabel(string? profileId, string? workingDirectory, string? label)
    {
        var key = LabelKey(profileId, workingDirectory);
        if (string.IsNullOrWhiteSpace(label))
        {
            _labels.Remove(key);
        }
        else
        {
            _labels[key] = label.Trim();
        }

        return Save();
    }

    private bool Save()
    {
        try
        {
            var labels = new JsonObject();
            foreach (var (key, value) in _labels.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                labels[key] = value;
            }

            var root = new JsonObject { ["version"] = 1, ["labels"] = labels };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, root.ToJsonString(Jsonc.SerializerOptions) + "\n", new System.Text.UTF8Encoding(false));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
