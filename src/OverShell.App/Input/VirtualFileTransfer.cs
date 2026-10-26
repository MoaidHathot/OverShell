using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using IDataObject = System.Windows.IDataObject;

namespace OverShell.App.Input;

/// <summary>Shell virtual files (mail attachments, archive entries): descriptors plus indexed FileContents.</summary>
internal static class VirtualFileTransfer
{
    public static bool IsPresent(IDataObject data) => data.GetDataPresent("FileGroupDescriptorW", false) || data.GetDataPresent("FileGroupDescriptor", false);

    public static unsafe IReadOnlyList<string> Read(IDataObject data, TransferFileStore store)
    {
        var unicode = data.GetDataPresent("FileGroupDescriptorW", false);
        var descriptor = data.GetData(unicode ? "FileGroupDescriptorW" : "FileGroupDescriptor", false);
        var entries = Parse(ReadBytes(descriptor), unicode);
        var roots = entries.Select(e => e.Name).Where(name => !entries.Any(other =>
            name.StartsWith(other.Name + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (entries.Length == 0) return [];

        // WPF's string-format GetData has no lindex argument. Query the native IDataObject
        // interface to request each file separately, preserving the original COM protocol.
        var unknown = Marshal.GetIUnknownForObject(data is DataObject ? data : new DataObject(data));
        nint source = 0;
        try
        {
            var iid = new Guid("0000010e-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out source));
            var getData = (delegate* unmanaged[Stdcall]<nint, NativeFormat*, NativeMedium*, int>)(*(nint**)source)[3];
            return store.SaveBatch(roots, directory =>
            {
                for (var index = 0; index < entries.Length; index++)
                {
                    var entry = entries[index];
                    var path = Path.Combine(directory, entry.Name);
                    if (entry.Directory) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var format = new NativeFormat { Format = (ushort)DataFormats.GetDataFormat("FileContents").Id, Aspect = 1, Index = index, Medium = 1 | 4 | 8 };
                    NativeMedium medium = default;
                    Marshal.ThrowExceptionForHR(getData(source, &format, &medium));
                    try
                    {
                        if (medium.Kind == 8) CopyStorage(medium.Value, path);
                        else
                        {
                            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            Copy(medium, output, entry.Length);
                        }
                    }
                    finally { ReleaseStgMedium(ref medium); }
                }
            });
        }
        finally
        {
            if (source != 0) Marshal.Release(source);
            Marshal.Release(unknown);
        }
    }

    private sealed record Entry(string Name, bool Directory, long? Length);

    private static Entry[] Parse(byte[] bytes, bool unicode)
    {
        var size = unicode ? 592 : 332; // FILEDESCRIPTORW/A: name starts at byte 72.
        if (bytes.Length < 4) throw new InvalidDataException("The virtual file descriptor is incomplete.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count > (bytes.Length - 4) / size) throw new InvalidDataException("The virtual file list is truncated.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = unicode ? Encoding.Unicode : Encoding.GetEncoding((int)GetACP());
        var entries = new Entry[count];
        for (var i = 0; i < entries.Length; i++)
        {
            var record = bytes.AsSpan(4 + i * size, size);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[36..]);
            var name = encoding.GetString(record[72..]).Split('\0')[0];
            if (Path.IsPathRooted(name)) throw new InvalidDataException("A virtual filename must be relative.");
            var parts = name.Replace('\\', '/').Split('/');
            if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or "..")) throw new InvalidDataException("A virtual filename contains an invalid path segment.");
            name = Path.Combine(parts.Select(TransferFileStore.SafeName).ToArray());
            long? length = null;
            if ((flags & 0x40) != 0)
            {
                var value = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(record[64..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(record[68..]);
                if (value > long.MaxValue) throw new InvalidDataException("The virtual file size is invalid.");
                length = (long)value;
            }

            entries[i] = new(name, (flags & 4) != 0 && (attributes & 0x10) != 0, length);
        }

        return entries;
    }

    private static byte[] ReadBytes(object? value)
    {
        if (value is byte[] bytes) return bytes;
        if (value is not Stream source) throw new InvalidDataException("No virtual file descriptor data was supplied.");
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

    private static void Copy(NativeMedium medium, Stream output, long? length)
    {
        if (medium.Kind == 1) // TYMED_HGLOBAL, which may have allocation padding.
        {
            var available = (ulong)GlobalSize(medium.Value);
            var count = length is { } expected ? (ulong)expected : available;
            if (count > available) throw new EndOfStreamException("The virtual file contents are truncated.");
            if (count == 0) return;
            var pointer = GlobalLock(medium.Value);
            if (pointer == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var buffer = new byte[65536];
                for (ulong offset = 0; offset < count;)
                {
                    var chunk = (int)Math.Min((ulong)buffer.Length, count - offset);
                    Marshal.Copy(pointer + (nint)offset, buffer, 0, chunk);
                    output.Write(buffer, 0, chunk);
                    offset += (uint)chunk;
                }
            }
            finally { GlobalUnlock(medium.Value); }
        }
        else if (medium.Kind == 4) // TYMED_ISTREAM
        {
            var stream = (IStream)Marshal.GetObjectForIUnknown(medium.Value);
            var read = Marshal.AllocCoTaskMem(4);
            try
            {
                try { stream.Seek(0, 0, 0); }
                catch (COMException e) when (e.HResult is unchecked((int)0x80030001) or unchecked((int)0x80004001)) { }
                var buffer = new byte[65536];
                long total = 0;
                while (length is null || total < length)
                {
                    var requested = (int)Math.Min(buffer.Length, length is { } size ? size - total : buffer.Length);
                    stream.Read(buffer, requested, read);
                    var count = Marshal.ReadInt32(read);
                    if (count < 0 || count > requested) throw new InvalidDataException("The virtual file stream returned an invalid byte count.");
                    if (count == 0) break;
                    output.Write(buffer, 0, count);
                    total += count;
                }

                if (length is { } expected && total != expected) throw new EndOfStreamException("The virtual file stream ended early.");
            }
            finally
            {
                Marshal.FreeCoTaskMem(read);
                if (Marshal.IsComObject(stream)) Marshal.ReleaseComObject(stream);
            }
        }
        else throw new NotSupportedException("The virtual file provider did not return a readable stream.");
    }

    private static unsafe void CopyStorage(nint source, string path)
    {
        // TYMED_ISTORAGE represents a compound document. Copy its complete storage tree
        // rather than treating an interface pointer as file bytes. Fail if a name exists.
        Marshal.ThrowExceptionForHR(StgCreateDocfile(path, 0x12, 0, out var destination));
        try
        {
            var copy = (delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, int>)(*(nint**)source)[7];
            Marshal.ThrowExceptionForHR(copy(source, 0, 0, 0, destination));
            var commit = (delegate* unmanaged[Stdcall]<nint, uint, int>)(*(nint**)destination)[9];
            Marshal.ThrowExceptionForHR(commit(destination, 0));
        }
        finally { Marshal.Release(destination); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFormat { public ushort Format; public nint Target; public uint Aspect; public int Index; public uint Medium; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMedium { public uint Kind; public nint Value; public nint Releaser; }

    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref NativeMedium medium);
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int StgCreateDocfile(string path, uint mode, uint reserved, out nint storage);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] private static extern uint GetACP();
}
