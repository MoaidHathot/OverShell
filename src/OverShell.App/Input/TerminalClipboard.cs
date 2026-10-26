using System.Windows;
using System.IO;
using System.Windows.Media.Imaging;

namespace OverShell.App.Input;

internal sealed record TransferContent(string Text, IReadOnlyList<string> Files, bool ApplicationPaste = false)
{
    public bool IsEmpty => Text.Length == 0 && Files.Count == 0 && !ApplicationPaste;
    public static TransferContent Empty { get; } = new(string.Empty, []);

    public void Deliver(Action<string> pasteText, Action<string> sendKey, Action<IReadOnlyList<string>> pasteFiles)
    {
        if (ApplicationPaste) sendKey("\u0016");
        else if (Files.Count > 0) pasteFiles(Files);
        else if (Text.Length > 0) pasteText(Text);
    }
}

/// <summary>One data reader for clipboard, native drops and the prompt bar. No file-extension filter.</summary>
internal static class TerminalClipboard
{
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PNG"] = ".png", ["image/png"] = ".png", ["JFIF"] = ".jpg", ["image/jpeg"] = ".jpg",
        ["GIF"] = ".gif", ["image/gif"] = ".gif", ["image/webp"] = ".webp", ["image/avif"] = ".avif",
        ["image/bmp"] = ".bmp", ["image/tiff"] = ".tiff", ["image/svg+xml"] = ".svg",
        ["application/pdf"] = ".pdf", ["audio/wav"] = ".wav", ["audio/x-wav"] = ".wav",
        ["WaveAudio"] = ".wav", ["RiffAudio"] = ".wav", ["audio/mpeg"] = ".mp3",
        ["audio/ogg"] = ".ogg", ["audio/flac"] = ".flac", ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm", ["video/quicktime"] = ".mov",
        ["HTML Format"] = ".html", ["Rich Text Format"] = ".rtf",
        ["TaggedImageFileFormat"] = ".tiff", ["DeviceIndependentBitmap"] = ".dib",
    };

    private static bool IsMetadata(string format) => format is
        "Preferred DropEffect" or "Performed DropEffect" or "Paste Succeeded" or "Shell IDList Array" or
        "FileName" or "FileNameW" or "FileGroupDescriptor" or "FileGroupDescriptorW" or "FileContents" or
        "DataObject" or "DataObjectAttributes" or "DataObjectAttributesRequiringElevation" or
        "InShellDragLoop" or "Logical Performed DropEffect" or "TargetCLSID" or "UntrustedDragDrop" or
        "DragWindow" or "DropDescription" or "Shell Object Offsets";

    public static bool CanRead(IDataObject data) => VirtualFileTransfer.IsPresent(data) || data.GetFormats().Any(f => !IsMetadata(f));

    public static TransferContent Read(IDataObject data, TransferFileStore store, bool asFiles = false, bool materializeImage = false)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            // Snapshot the array before the clipboard changes; preserve order and every type.
            var files = paths.Select(Path.GetFullPath).ToArray();
            return new(string.Empty, Array.AsReadOnly(files));
        }

        if (VirtualFileTransfer.IsPresent(data)) return new(string.Empty, VirtualFileTransfer.Read(data, store));

        var image = data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG") || data.GetDataPresent("image/png");
        if (image && !asFiles && !materializeImage) return new(string.Empty, [], ApplicationPaste: true);

        foreach (var format in new[] { "PNG", "image/png" })
        {
            var value = data.GetData(format, autoConvert: false);
            if (value is Stream or byte[]) return File(store.SaveData("image.png", value));
        }

        if (image && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            return File(store.Save("image.png", stream =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }));
        }

        var formats = data.GetFormats(autoConvert: false).Where(f => !IsMetadata(f)).ToArray();
        // Raw media takes precedence over an accompanying caption or URL. Ordinary rich
        // text still uses its plain-text representation unless file delivery was requested.
        foreach (var format in formats.Where(f => Extensions.ContainsKey(f) && f != DataFormats.Html && f != DataFormats.Rtf))
        {
            var value = data.GetData(format, autoConvert: false);
            if (value is Stream or byte[] or string) return File(store.SaveData("clipboard" + Extensions[format], value));
        }

        if (!asFiles && data.GetData(DataFormats.UnicodeText) is string text) return new(text, []);

        // Prefer a provider's standard representation, then preserve any other raw payload
        // as a binary file. This supports media and private formats without an agent allowlist.
        foreach (var format in formats.OrderByDescending(Extensions.ContainsKey))
        {
            if (format == DataFormats.Bitmap || format == DataFormats.FileDrop) continue;
            var value = data.GetData(format, autoConvert: false);
            if (value is not (Stream or byte[] or string)) continue;
            var extension = Extensions.GetValueOrDefault(format) ?? (value is string ? ".txt" : ".bin");
            return File(store.SaveData("clipboard" + extension, value));
        }

        if (formats.Length > 0) throw new NotSupportedException("The clipboard provider supplied no readable file, text or media data.");
        return TransferContent.Empty;
    }

    public static void Paste(IDataObject data, Action<string> pasteText, Action<string> sendKey,
        Action<IReadOnlyList<string>>? pasteFiles = null, TransferFileStore? store = null, bool asFiles = false)
    {
        var content = Read(data, store ?? new TransferFileStore(Core.AppPaths.ClipboardDir), asFiles);
        content.Deliver(pasteText, sendKey, pasteFiles ?? (_ => throw new InvalidOperationException("File delivery requires a target.")));
    }

    private static TransferContent File(string path) => new(string.Empty, Array.AsReadOnly(new[] { path }));
}
