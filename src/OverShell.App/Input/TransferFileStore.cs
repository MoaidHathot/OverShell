using System.IO;
using System.Text;

namespace OverShell.App.Input;

/// <summary>Persistent local files for clipboard-only content, usable after a tab or app restart.</summary>
internal sealed class TransferFileStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string Save(string name, Action<Stream> write)
    {
        // Providers can supply a filename, never a directory or a device name.
        name = Path.GetFileName(name.Replace('\\', '/'));
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) name = "content.bin";
        var stem = Path.GetFileNameWithoutExtension(name).TrimEnd('.', ' ');
        if (stem.Equals("nul", StringComparison.OrdinalIgnoreCase) || stem.Equals("con", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("prn", StringComparison.OrdinalIgnoreCase) || stem.Equals("aux", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("com", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("lpt", StringComparison.OrdinalIgnoreCase)) && char.IsAsciiDigit(stem[3])))
        {
            name = "clipboard-" + name;
        }

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
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
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
