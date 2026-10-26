using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using OverShell.App.Input;
using Xunit;

namespace OverShell.Tests;

public class VirtualFileTests
{
    [Fact]
    public unsafe void Compound_document_virtual_files_keep_their_storage_contents() => WithStore(store =>
    {
        byte[] expected = [0, 128, 255, 13, 10];
        var source = new VirtualFiles([new("message.msg", expected)]) { UseStorage = true };
        var path = Assert.Single(TerminalClipboard.Read(new DataObject(source), store).Files);
        Marshal.ThrowExceptionForHR(StgOpenStorage(path, 0, 0x10, 0, 0, out var storage));
        var name = Marshal.StringToCoTaskMemUni("payload");
        nint stream = 0;
        try
        {
            var open = (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, uint, nint*, int>)(*(nint**)storage)[4];
            Marshal.ThrowExceptionForHR(open(storage, name, 0, 0x10, 0, &stream));
            var read = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)(*(nint**)stream)[3];
            var actual = new byte[expected.Length];
            uint count = 0;
            fixed (byte* bytes = actual) Marshal.ThrowExceptionForHR(read(stream, bytes, (uint)actual.Length, &count));
            Assert.Equal((uint)expected.Length, count);
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (stream != 0) Marshal.Release(stream);
            Marshal.FreeCoTaskMem(name);
            Marshal.Release(storage);
        }
    });

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Virtual_file_contents_are_requested_by_index_and_preserved(bool streams, bool unicode) => WithStore(store =>
    {
        var firstName = unicode ? "日本語 report.pdf" : "report.pdf";
        var source = new VirtualFiles([new(firstName, [0, 255, 13, 10]), new("movie.unknown", [27, 128, 0])], streams, unicode);
        var data = new DataObject(source);
        Assert.True(TerminalClipboard.CanRead(data));
        var content = TerminalClipboard.Read(data, store);
        Assert.Equal(2, content.Files.Count);
        Assert.Equal(firstName, Path.GetFileName(content.Files[0]));
        Assert.Equal("movie.unknown", Path.GetFileName(content.Files[1]));
        Assert.Equal(source.Files[0].Bytes, File.ReadAllBytes(content.Files[0]));
        Assert.Equal(source.Files[1].Bytes, File.ReadAllBytes(content.Files[1]));
        Assert.Equal([0, 1], source.RequestedIndices);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Empty_virtual_files_are_kept(bool streams) => WithStore(store =>
    {
        var source = new VirtualFiles([new("empty.any", [])], streams);
        var path = Assert.Single(TerminalClipboard.Read(new DataObject(source), store).Files);
        Assert.Equal(0, new FileInfo(path).Length);
    });

    [Fact]
    public void Virtual_directories_keep_their_hierarchy_and_only_roots_are_delivered() => WithStore(store =>
    {
        var source = new VirtualFiles([new("folder", null), new(@"folder\image.avif", [1, 2]), new(@"folder\sub", null), new(@"folder\sub\data.bin", [3, 0]), new("loose.mp3", [4])]);
        var result = TerminalClipboard.Read(new DataObject(source), store);
        Assert.Equal(2, result.Files.Count);
        Assert.True(Directory.Exists(result.Files[0]));
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(result.Files[0], "image.avif")));
        Assert.Equal(new byte[] { 3, 0 }, File.ReadAllBytes(Path.Combine(result.Files[0], "sub", "data.bin")));
        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(result.Files[1]));
        Assert.Equal([1, 3, 4], source.RequestedIndices);
    });

    [Fact]
    public void A_failed_virtual_file_rolls_back_the_whole_batch() => WithStore(store =>
    {
        var source = new VirtualFiles([new("one.txt", [1]), new("two.mp4", [2])]) { FailIndex = 1 };
        Assert.Throws<COMException>(() => TerminalClipboard.Read(new DataObject(source), store));
        Assert.Empty(Directory.EnumerateFileSystemEntries(store.Root));
    });

    [Theory]
    [InlineData(@"..\escape.pdf")]
    [InlineData(@"C:\absolute.pdf")]
    public void Invalid_virtual_paths_are_rejected_before_export(string name) => WithStore(store =>
    {
        var source = new VirtualFiles([new(name, [1])]);
        Assert.Throws<InvalidDataException>(() => TerminalClipboard.Read(new DataObject(source), store));
        Assert.False(Directory.Exists(store.Root));
    });

    [Fact]
    public void Truncated_descriptors_are_rejected() => WithStore(store =>
    {
        var data = new DataObject("FileGroupDescriptorW", new MemoryStream(new byte[] { 2, 0, 0, 0, 1 }), false);
        Assert.Throws<InvalidDataException>(() => TerminalClipboard.Read(data, store));
    });

    private static void WithStore(Action<TransferFileStore> action) => Sta.Run(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "overshell-virtual-" + Guid.NewGuid().ToString("N"));
        try { action(new TransferFileStore(root)); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    internal sealed record VirtualFile(string Name, byte[]? Bytes);

    // An actual native IDataObject provider, including indexed IStream/HGLOBAL FileContents.
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class VirtualFiles : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        private readonly bool _streams;
        private readonly bool _unicode;
        private readonly short _descriptors;
        private readonly short _contents;
        public VirtualFile[] Files { get; }
        public int FailIndex { get; init; } = -1;
        public bool UseStorage { get; init; }
        public List<int> RequestedIndices { get; } = [];

        public VirtualFiles(VirtualFile[] files, bool streams = true, bool unicode = true)
        {
            Files = files;
            _streams = streams;
            _unicode = unicode;
            _descriptors = (short)DataFormats.GetDataFormat(unicode ? "FileGroupDescriptorW" : "FileGroupDescriptor").Id;
            _contents = (short)DataFormats.GetDataFormat("FileContents").Id;
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            if (format.cfFormat == _descriptors)
            {
                var size = _unicode ? 592 : 332;
                var bytes = new byte[4 + Files.Length * size];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, Files.Length);
                for (var i = 0; i < Files.Length; i++)
                {
                    var entry = Files[i];
                    var record = bytes.AsSpan(4 + i * size, size);
                    BinaryPrimitives.WriteInt32LittleEndian(record, 4 | 0x40);
                    BinaryPrimitives.WriteInt32LittleEndian(record[36..], entry.Bytes is null ? 0x10 : 0x80);
                    BinaryPrimitives.WriteInt32LittleEndian(record[68..], entry.Bytes?.Length ?? 0);
                    (_unicode ? Encoding.Unicode : Encoding.ASCII).GetBytes(entry.Name).CopyTo(record[72..]);
                }

                medium = Memory(bytes);
                return;
            }

            if (format.cfFormat != _contents || format.lindex < 0 || format.lindex >= Files.Length)
                throw new COMException("Format unavailable", unchecked((int)0x80040064));
            RequestedIndices.Add(format.lindex);
            if (format.lindex == FailIndex) throw new COMException("File provider failed", unchecked((int)0x80004005));
            var content = Files[format.lindex].Bytes!;
            medium = UseStorage ? Storage(content) : _streams
                ? new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = SHCreateMemStream(content, (uint)content.Length) }
                : Memory(content);
        }

        private static unsafe STGMEDIUM Storage(byte[] content)
        {
            Marshal.ThrowExceptionForHR(StgCreateDocfile(null, 0x04001012, 0, out var storage));
            var name = Marshal.StringToCoTaskMemUni("payload");
            nint stream = 0;
            try
            {
                var create = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, nint*, int>)(*(nint**)storage)[3];
                Marshal.ThrowExceptionForHR(create(storage, name, 0x1012, 0, 0, &stream));
                var write = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)(*(nint**)stream)[4];
                uint written;
                fixed (byte* bytes = content) Marshal.ThrowExceptionForHR(write(stream, bytes, (uint)content.Length, &written));
                var commit = (delegate* unmanaged[Stdcall]<nint, uint, int>)(*(nint**)storage)[9];
                Marshal.ThrowExceptionForHR(commit(storage, 0));
                return new STGMEDIUM { tymed = TYMED.TYMED_ISTORAGE, unionmember = storage };
            }
            catch { Marshal.Release(storage); throw; }
            finally
            {
                if (stream != 0) Marshal.Release(stream);
                Marshal.FreeCoTaskMem(name);
            }
        }

        private static STGMEDIUM Memory(byte[] bytes)
        {
            var memory = GlobalAlloc(2, (nuint)bytes.Length);
            if (bytes.Length == 0) return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory };
            var pointer = GlobalLock(memory);
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(memory); }
            return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory };
        }

        public int QueryGetData(ref FORMATETC format) => format.cfFormat == _descriptors || format.cfFormat == _contents ? 0 : unchecked((int)0x80040064);
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => new Formats([
            new() { cfFormat = _descriptors, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
            new() { cfFormat = _contents, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM | TYMED.TYMED_ISTORAGE },
        ]);
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = 0; return 0x40130; }
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) { }
        public int EnumDAdvise(out IEnumSTATDATA? enumerator) { enumerator = null; return unchecked((int)0x80040003); }

        [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
        [DllImport("shlwapi.dll")] private static extern nint SHCreateMemStream(byte[] bytes, uint length);
        [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int StgCreateDocfile(string? name, uint mode, uint reserved, out nint storage);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Formats(FORMATETC[] formats) : IEnumFORMATETC
    {
        private int _index;
        public int Next(int count, FORMATETC[] values, int[] fetched)
        {
            var read = 0;
            while (read < count && _index < formats.Length) values[read++] = formats[_index++];
            if (fetched.Length > 0) fetched[0] = read;
            return read == count ? 0 : 1;
        }
        public int Skip(int count) { _index += count; return _index <= formats.Length ? 0 : 1; }
        public int Reset() { _index = 0; return 0; }
        public void Clone(out IEnumFORMATETC copy) => copy = new Formats(formats) { _index = _index };
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int StgOpenStorage(string name, nint priority, uint mode, nint exclude, uint reserved, out nint storage);
}
