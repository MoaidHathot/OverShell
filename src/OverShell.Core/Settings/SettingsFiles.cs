namespace OverShell.Core.Settings;

/// <summary>
/// The user's <c>settings.jsonc</c> and <c>keybindings.jsonc</c>, written from the embedded
/// defaults when they do not exist: fully commented, every value the default, so a fresh file
/// changes nothing until it is edited and each line says what it is for. One writer for the
/// CLI (<c>OverShell settings init</c>), the palette and the setting pickers, so the header and
/// the rules are the same wherever the file came from.
/// </summary>
public static class SettingsFiles
{
    /// <summary>The two files and the embedded default each starts from.</summary>
    public static IReadOnlyList<(string Path, string Resource)> Defaults =>
    [
        (AppPaths.SettingsFile, "settings.jsonc"),
        (AppPaths.KeybindingsFile, "keybindings.jsonc"),
    ];

    /// <summary>
    /// Writes <paramref name="path"/> from <paramref name="resource"/> unless it exists already.
    /// <paramref name="writtenBy"/> names the writer in the header ("OverShell", "`OverShell
    /// settings init`"). True when the file was written. IO errors propagate: the caller says
    /// where it was trying to write.
    /// </summary>
    public static bool WriteDefault(string path, string resource, string writtenBy)
    {
        if (File.Exists(path))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // LF throughout: the embedded file is LF, and a header in the other convention would make
        // a mixed file that some editors flag on every save.
        var header = $"// Written by {writtenBy} from the built-in defaults. Every value here equals the default, so this\n" +
                     "// file changes nothing until you edit it; delete a line to fall back. Saved changes apply live.\n";
        File.WriteAllText(path, header + EmbeddedResources.Read(resource).Replace("\r\n", "\n", StringComparison.Ordinal), new System.Text.UTF8Encoding(false));
        return true;
    }

    /// <summary>Both files; returns the names of those written (none when both existed).</summary>
    public static IReadOnlyList<string> WriteDefaults(string writtenBy)
    {
        var written = new List<string>();
        foreach (var (path, resource) in Defaults)
        {
            if (WriteDefault(path, resource, writtenBy))
            {
                written.Add(Path.GetFileName(path));
            }
        }

        return written;
    }

    /// <summary>
    /// Sets one value in the user's <c>settings.jsonc</c> and saves it - the file written from the
    /// defaults first when it does not exist, so the key lands among its comments rather than in a
    /// bare <c>{ }</c>. Encoding (a UTF-8 BOM stays a BOM) and line endings are the file's own;
    /// the write goes through a temporary file, so a reader never sees half a file. False with
    /// <paramref name="error"/> when nothing was written.
    /// </summary>
    public static bool Set(IReadOnlyList<string> path, string json, string writtenBy, out string? error)
    {
        error = null;
        var file = AppPaths.SettingsFile;
        try
        {
            WriteDefault(file, "settings.jsonc", writtenBy);
            var bytes = File.ReadAllBytes(file);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var encoding = new System.Text.UTF8Encoding(bom);
            var text = encoding.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var edited = JsoncEdit.Set(text, path, json, out error);
            if (edited is null)
            {
                return false;
            }

            var temp = file + ".tmp";
            File.WriteAllText(temp, edited, encoding);
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// What the settings currently say: the user's file over the defaults, as the loader merges
    /// them, plus which keys the user's file sets itself.
    /// </summary>
    public static SettingsSnapshot Snapshot()
    {
        var defaults = Jsonc.Parse(EmbeddedResources.Read("settings.jsonc"), out _);
        var user = File.Exists(AppPaths.SettingsFile) ? Jsonc.ReadFile(AppPaths.SettingsFile, out _) : null;
        return new SettingsSnapshot(Jsonc.Merge(defaults, user), user);
    }
}

/// <summary>The merged settings and the user's own layer, for showing a setting's current value.</summary>
public sealed record SettingsSnapshot(System.Text.Json.Nodes.JsonNode? Merged, System.Text.Json.Nodes.JsonNode? User)
{
    /// <summary>The effective value at <paramref name="path"/>, or null when nothing sets it.</summary>
    public System.Text.Json.Nodes.JsonNode? Current(IReadOnlyList<string> path) => Walk(Merged, path);

    /// <summary>True when the user's file names the key itself (so the value is theirs, not the default).</summary>
    public bool IsUserSet(IReadOnlyList<string> path) => Walk(User, path) is not null || HasExplicitNull(User, path);

    private static System.Text.Json.Nodes.JsonNode? Walk(System.Text.Json.Nodes.JsonNode? node, IReadOnlyList<string> path)
    {
        foreach (var segment in path)
        {
            if (node is not System.Text.Json.Nodes.JsonObject obj || !obj.TryGetPropertyValue(segment, out node))
            {
                return null;
            }
        }

        return node;
    }

    // `"skin": null` is a choice too (it removes the key), and Walk cannot tell it from absence.
    private static bool HasExplicitNull(System.Text.Json.Nodes.JsonNode? node, IReadOnlyList<string> path)
    {
        for (var i = 0; i < path.Count; i++)
        {
            if (node is not System.Text.Json.Nodes.JsonObject obj || !obj.ContainsKey(path[i]))
            {
                return false;
            }

            node = obj[path[i]];
            if (i == path.Count - 1)
            {
                return node is null;
            }
        }

        return false;
    }
}
