using System.Runtime.InteropServices;
using System.Windows;

namespace OverShell.App.Input;

[StructLayout(LayoutKind.Sequential)]
public struct DropPoint
{
    public int X;
    public int Y;
}

// Public for the COM callable wrapper. POINTL is two 32-bit coordinates even on x64.
[ComVisible(true), Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] object data, uint keys, DropPoint point, ref uint effect);
    [PreserveSig] int DragOver(uint keys, DropPoint point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] object data, uint keys, DropPoint point, ref uint effect);
}

/// <summary>OLE drop registration for the native child HWND, which WPF routed drops do not cover.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class NativeDropTarget : IOleDropTarget, IDisposable
{
    private readonly Func<IDataObject, bool> _canAccept;
    private readonly Func<IDataObject, bool> _accept;
    private readonly Action<Exception> _failed;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _canDrop;
    private bool _disposed;

    public NativeDropTarget(nint hwnd, Func<IDataObject, bool> canAccept, Func<IDataObject, bool> accept, Action<Exception> failed)
    {
        Hwnd = hwnd;
        _canAccept = canAccept;
        _accept = accept;
        _failed = failed;
        Marshal.ThrowExceptionForHR(OleInitialize(0));
        try { Marshal.ThrowExceptionForHR(RegisterDragDrop(hwnd, this)); }
        catch { OleUninitialize(); throw; }
    }

    public nint Hwnd { get; }
    public bool IsRegistered => !_disposed;

    public int DragEnter(object data, uint keys, DropPoint point, ref uint effect)
    {
        try { _canDrop = !_disposed && _canAccept(Wrap(data)); }
        catch (Exception e) { _canDrop = false; Report(e); }
        effect = _canDrop ? AllowedEffect(effect) : 0;
        return 0;
    }

    public int DragOver(uint keys, DropPoint point, ref uint effect)
    {
        effect = !_disposed && _canDrop ? AllowedEffect(effect) : 0;
        return 0;
    }

    public int DragLeave()
    {
        _canDrop = false;
        return 0;
    }

    public int Drop(object data, uint keys, DropPoint point, ref uint effect)
    {
        var allowed = AllowedEffect(effect);
        effect = 0;
        try
        {
            // Read the data synchronously while the source owns it. A consumer may queue
            // its captured content, but must not retain this apartment-bound data object.
            if (!_disposed && allowed != 0)
            {
                var wrapped = Wrap(data);
                if (_canAccept(wrapped) && _accept(wrapped)) effect = allowed;
            }
        }
        catch (Exception e) { Report(e); }
        finally { _canDrop = false; }
        return 0;
    }

    // Never return MOVE: terminal file references must not tell Explorer to delete originals.
    private static uint AllowedEffect(uint offered) => (offered & 1) != 0 ? 1u : (offered & 4) != 0 ? 4u : 0u;
    private static IDataObject Wrap(object data) => data as IDataObject ?? new DataObject(data);

    private void Report(Exception e)
    {
        try { _failed(e); }
        catch { /* No managed exception may escape an OLE callback. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Dispose a drop target on its owning UI thread.");
        _disposed = true;
        _canDrop = false;
        _ = RevokeDragDrop(Hwnd);
        OleUninitialize();
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint hwnd, [MarshalAs(UnmanagedType.Interface)] IOleDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint hwnd);
}
