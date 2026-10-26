using System.IO;
using System.Text;

namespace OverShell.App.Input;

/// <summary>Persistent local files for clipboard-only content, usable after a tab or app restart.</summary>
internal sealed class TransferFileStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string Save(string name, Action<Stream> write)
    {
        name = SafeName(name);
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var staging = path + ".part";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(stream);
            File.Move(staging, path);
            return path;
        }
        catch
        {
            RemoveIncomplete(directory);
            throw;
        }
    }

    internal static string SafeName(string name)
    {
        // Providers can supply a filename, never a directory or a device name.
        name = Path.GetFileName(name.Replace('\\', '/'));
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) name = "content.bin";
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("nul", StringComparison.OrdinalIgnoreCase) || stem.Equals("con", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("prn", StringComparison.OrdinalIgnoreCase) || stem.Equals("aux", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("conin$", StringComparison.OrdinalIgnoreCase) || stem.Equals("conout$", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("com", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("lpt", StringComparison.OrdinalIgnoreCase)) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
        {
            name = "clipboard-" + name;
        }

        return name;
    }

    internal IReadOnlyList<string> SaveBatch(IReadOnlyList<string> relativeRoots, Action<string> populate)
    {
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Paths are published only after the whole batch succeeds. Renaming a populated
            // directory can be denied on Windows while other file-system observers inspect it.
            populate(directory);
            return Array.AsReadOnly(relativeRoots.Select(path => Path.Combine(directory, path)).ToArray());
        }
        catch
        {
            RemoveIncomplete(directory);
            throw;
        }
    }

    private static void RemoveIncomplete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public string SaveText(string name, string text) => Save(name, stream => stream.Write(Encoding.UTF8.GetBytes(text)));

    public string SaveData(string name, object data) => Save(name, destination =>
    {
        if (data is byte[] bytes)
        {
            destination.Write(bytes);
        }
        else if (data is Stream source)
        {
            // Data objects often leave streams at their end. Keep the provider's stream alive
            // and restore its position; another format or another target may use it next.
            var position = source.CanSeek ? source.Position : (long?)null;
            try
            {
                if (source.CanSeek) source.Position = 0;
                source.CopyTo(destination);
            }
            finally
            {
                if (position is { } old) source.Position = old;
            }
        }
        else if (data is string text)
        {
            destination.Write(Encoding.UTF8.GetBytes(text));
        }
        else throw new NotSupportedException("The clipboard provider did not supply a readable stream.");
    });
}
