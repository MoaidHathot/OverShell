using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OverShell.Core.Previews;

namespace OverShell.App.Terminal.WindowsTerminal;

/// <summary>Versioned, optional preview ABI. All calls and leases belong to the terminal's dispatcher.</summary>
internal static class NativeTerminalPreview
{
    private const string Library = "Microsoft.Terminal.Control.dll";
    internal static readonly bool Available = FindApi();
    internal static int ActiveLeases { get; private set; }
    internal static long SnapshotCount { get; private set; }
    internal static long GeometryUpdates { get; private set; }

    private static bool FindApi()
    {
        if (!NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, Library), out var module)) return false;
        try
        {
            return new[] { "TerminalCreatePreview", "TerminalUpdatePreview", "TerminalDestroyPreview", "TerminalGetPreviewSnapshot", "TerminalFreePreviewSnapshot" }
                .All(name => NativeLibrary.TryGetExport(module, name, out _));
        }
        finally { NativeLibrary.Free(module); }
    }

    internal static unsafe TerminalPreviewFrame? Capture(nint source)
    {
        if (!Available || source == 0 || TerminalGetPreviewSnapshot(source, out var memory) != 0 || memory == 0) return null;
        try
        {
            var size = Marshal.ReadInt32(memory);
            if (size < 32 || size > TerminalPreviewFrame.MaximumBytes) return null;
            SnapshotCount++;
            return TerminalPreviewFrame.Decode(new ReadOnlySpan<byte>((void*)memory, size));
        }
        finally { TerminalFreePreviewSnapshot(memory); }
    }

    internal static Lease? Create(nint source, nint destination, Dispatcher dispatcher)
    {
        dispatcher.VerifyAccess();
        if (!Available || source == 0 || destination == 0 || TerminalCreatePreview(source, destination, out var token) != 0 || token == 0) return null;
        ActiveLeases++;
        return new Lease(token, dispatcher);
    }

    internal sealed class Lease(ulong token, Dispatcher dispatcher) : IDisposable
    {
        private ulong _token = token;
        private (PixelRect Destination, PixelRect Clip, float Radius)? _last;

        public bool Update(Rect destination, Rect clip, double radius)
        {
            dispatcher.VerifyAccess();
            if (_token == 0 || destination.IsEmpty || clip.IsEmpty) return false;
            var dest = PixelRect.From(destination);
            var visible = PixelRect.From(clip);
            var corner = (float)Math.Max(0, radius);
            if (_last == (dest, visible, corner)) return true;
            var hr = TerminalUpdatePreview(_token, in dest, in visible, corner);
            if (hr != 0) return false;
            _last = (dest, visible, corner);
            GeometryUpdates++;
            return true;
        }

        public void Dispose()
        {
            dispatcher.VerifyAccess();
            if (_token == 0) return;
            _ = TerminalDestroyPreview(_token);
            _token = 0;
            ActiveLeases--;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
    {
        public static PixelRect From(Rect rect) => new((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top), (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
    }

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern int TerminalCreatePreview(nint source, nint destination, out ulong token);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern int TerminalUpdatePreview(ulong token, in PixelRect destination, in PixelRect clip, float radius);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern int TerminalDestroyPreview(ulong token);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern int TerminalGetPreviewSnapshot(nint source, out nint snapshot);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern void TerminalFreePreviewSnapshot(nint snapshot);
}
