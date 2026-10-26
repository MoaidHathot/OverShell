using System.Runtime.InteropServices;
using System.Windows;
using OverShell.App.Input;
using Xunit;

namespace OverShell.Tests;

public class NativeDropTests
{
    [Fact]
    public unsafe void Native_com_callback_marshals_an_actual_ole_data_object() => Sta.Run(() =>
    {
        using var window = CreateWindow();
        var failures = new List<Exception>();
        string? received = null;
        using var target = new NativeDropTarget(window.Handle, TerminalClipboard.CanRead,
            data => { received = data.GetData(DataFormats.UnicodeText) as string; return received is not null; }, failures.Add);
        var source = new DataObject(DataFormats.UnicodeText, "native OLE 日本語");
        var sourceUnknown = Marshal.GetIUnknownForObject(source);
        var targetPointer = Marshal.GetComInterfaceForObject(target, typeof(IOleDropTarget));
        nint sourcePointer = 0;
        try
        {
            var iid = new Guid("0000010e-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(sourceUnknown, in iid, out sourcePointer));
            var table = *(nint**)targetPointer;
            var drop = (delegate* unmanaged[Stdcall]<nint, nint, uint, DropPoint, uint*, int>)table[6];
            uint effect = 1;
            Assert.Equal(0, drop(targetPointer, sourcePointer, 0, default, &effect));
            Assert.Equal(1u, effect);
            Assert.Equal("native OLE 日本語", received);
            Assert.Empty(failures);
        }
        finally
        {
            if (sourcePointer != 0) Marshal.Release(sourcePointer);
            Marshal.Release(targetPointer);
            Marshal.Release(sourceUnknown);
        }
    });

    [Fact]
    public void Registers_the_real_hwnd_and_revokes_it_on_disposal() => Sta.Run(() =>
    {
        using var window = CreateWindow();
        using (var target = CreateTarget(window.Handle, _ => true))
        {
            Assert.True(target.IsRegistered);
            Assert.Throws<COMException>(() => CreateTarget(window.Handle, _ => true));
        }

        using var replacement = CreateTarget(window.Handle, _ => true);
        Assert.True(replacement.IsRegistered);
    });

    [Theory]
    [InlineData(7u, 1u)]
    [InlineData(2u, 0u)]
    [InlineData(4u, 4u)]
    [InlineData(0u, 0u)]
    public void Drops_accept_content_once_without_moving_source_files(uint offered, uint expected) => Sta.Run(() =>
    {
        using var window = CreateWindow();
        var deliveries = 0;
        using var target = CreateTarget(window.Handle, data =>
        {
            Assert.Equal(new[] { @"C:\movie.mp4", @"C:\unknown.format" }, data.GetData(DataFormats.FileDrop));
            deliveries++;
            return true;
        });
        var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\movie.mp4", @"C:\unknown.format" });
        var effect = offered;
        Assert.Equal(0, target.DragEnter(data, 0, default, ref effect));
        Assert.Equal(expected, effect);
        effect = offered;
        target.DragOver(4, default, ref effect); // Shift must never turn a reference into a move.
        Assert.Equal(expected, effect);
        effect = offered;
        Assert.Equal(0, target.Drop(data, 4, default, ref effect));
        Assert.Equal(expected, effect);
        Assert.Equal(expected == 0 ? 0 : 1, deliveries);
    });

    [Fact]
    public void Provider_errors_and_stale_callbacks_are_rejected_without_escaping_to_com() => Sta.Run(() =>
    {
        using var window = CreateWindow();
        var failures = new List<Exception>();
        var target = new NativeDropTarget(window.Handle, _ => true, _ => throw new IOException("failed provider"), failures.Add);
        uint effect = 1;
        var data = new DataObject(DataFormats.UnicodeText, "drop");
        Assert.Equal(0, target.Drop(data, 0, default, ref effect));
        Assert.Equal(0u, effect);
        Assert.Single(failures);
        target.Dispose();
        effect = 1;
        target.Drop(data, 0, default, ref effect);
        Assert.Equal(0u, effect);
        Assert.Single(failures);
    });

    [Fact]
    public void Leaving_clears_feedback_without_reading_or_saving_data() => Sta.Run(() =>
    {
        using var window = CreateWindow();
        using var target = CreateTarget(window.Handle, _ => throw new Exception("Unexpected delivery"));
        uint effect = 1;
        target.DragEnter(new DataObject(DataFormats.UnicodeText, "text"), 0, default, ref effect);
        target.DragLeave();
        effect = 1;
        target.DragOver(0, default, ref effect);
        Assert.Equal(0u, effect);
    });

    private static NativeTestWindow CreateWindow() => new();

    private static NativeDropTarget CreateTarget(nint hwnd, Func<IDataObject, bool> accept) =>
        new(hwnd, TerminalClipboard.CanRead, accept, e => Assert.Fail(e.ToString()));

    // HwndSource registers WPF's own drop target. The terminal is a native HWND instead.
    private sealed class NativeTestWindow : IDisposable
    {
        public nint Handle { get; } = CreateWindowExW(0, "STATIC", "OverShell drop test", 0, 0, 0, 1, 1, 0, 0, 0, 0);

        public NativeTestWindow()
        {
            if (Handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose() => DestroyWindow(Handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint hwnd);
    }
}
