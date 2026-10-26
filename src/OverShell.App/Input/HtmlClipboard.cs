using System.Globalization;
using System.IO;
using System.Text;

namespace OverShell.App.Input;

/// <summary>CF_HTML is an envelope with UTF-8 byte offsets, not a standalone HTML file.</summary>
internal static class HtmlClipboard
{
    public static string Save(TransferFileStore store, object value)
    {
        var document = Extract(ReadBytes(value));
        return store.Save("clipboard.html", output =>
        {
            // CF_HTML is always UTF-8. A signature keeps standalone files unambiguous even
            // when the copied context has no charset declaration of its own.
            if (!document.AsSpan().StartsWith(Encoding.UTF8.Preamble)) output.Write(Encoding.UTF8.Preamble);
            output.Write(document);
        });
    }

    private static byte[] Extract(byte[] bytes)
    {
        var position = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var envelope = false;
        while (position < bytes.Length)
        {
            var length = bytes.AsSpan(position).IndexOfAny((byte)'\r', (byte)'\n');
            if (length < 0) length = bytes.Length - position;
            var line = Encoding.ASCII.GetString(bytes, position, length);
            var colon = line.IndexOf(':');
            if (colon < 0) break;
            var key = line[..colon].Trim();
            var offset = key.Equals("StartHTML", StringComparison.OrdinalIgnoreCase) || key.Equals("EndHTML", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("StartFragment", StringComparison.OrdinalIgnoreCase) || key.Equals("EndFragment", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("StartSelection", StringComparison.OrdinalIgnoreCase) || key.Equals("EndSelection", StringComparison.OrdinalIgnoreCase);
            if (!offset && !key.Equals("Version", StringComparison.OrdinalIgnoreCase) && !key.Equals("SourceURL", StringComparison.OrdinalIgnoreCase)) break;
            if (offset)
            {
                envelope = true;
                offsets[key] = int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : -1;
            }

            position += length;
            if (position < bytes.Length && bytes[position] == '\r') position++;
            if (position < bytes.Length && bytes[position] == '\n') position++;
        }

        if (!envelope) return bytes;
        if (Range("StartHTML", "EndHTML") is { } context) return context;
        if (Range("StartFragment", "EndFragment") is { } fragment) return fragment;

        // Some providers emit stale offsets. The standard's redundant markers still
        // identify the selected fragment without exporting the transport header as text.
        ReadOnlySpan<byte> open = "<!--StartFragment-->"u8;
        ReadOnlySpan<byte> close = "<!--EndFragment-->"u8;
        var start = bytes.AsSpan(position).IndexOf(open);
        if (start >= 0)
        {
            start += position + open.Length;
            var end = bytes.AsSpan(start).IndexOf(close);
            if (end >= 0) return bytes.AsSpan(start, end).ToArray();
        }

        throw new InvalidDataException("The clipboard HTML has no valid document or fragment bounds.");

        byte[]? Range(string from, string to) => offsets.TryGetValue(from, out var first) && offsets.TryGetValue(to, out var last) &&
            first >= position && last >= first && last <= bytes.Length ? bytes.AsSpan(first, last - first).ToArray() : null;
    }

    private static byte[] ReadBytes(object value)
    {
        if (value is string text) return Encoding.UTF8.GetBytes(text);
        if (value is byte[] bytes) return bytes;
        if (value is not Stream source) throw new InvalidDataException("The HTML clipboard data is not readable.");
        var position = source.CanSeek ? source.Position : (long?)null;
        try
        {
            if (source.CanSeek) source.Position = 0;
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            return copy.ToArray();
        }
        finally { if (position is { } old) source.Position = old; }
    }
}
