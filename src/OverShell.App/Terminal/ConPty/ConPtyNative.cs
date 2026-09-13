using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OverShell.App.Terminal.ConPty;

/// <summary>
/// The handful of Win32 calls a pseudoconsole session needs. Source-generated
/// (<see cref="LibraryImportAttribute"/>) so there is no runtime IL stub generation.
/// <para>
/// The pseudoconsole entry points bind to <c>conpty.dll</c>, the redistributable from the
/// <c>Microsoft.Windows.Console.ConPTY</c> package, which launches the <c>OpenConsole.exe</c>
/// shipped next to it — the same host Windows Terminal uses — instead of whatever
/// <c>conhost.exe</c> the OS happens to have. If the DLL is missing the resolver falls back
/// to the in-box implementation in <c>kernel32.dll</c>, which has the same signatures.
/// </para>
/// </summary>
internal static unsafe partial class ConPtyNative
{
    private const string Kernel32 = "kernel32.dll";
    private const string ConPtyDll = "conpty.dll";

    public const uint ExtendedStartupInfoPresent = 0x00080000;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const nuint ProcThreadAttributePseudoConsole = 0x00020016;
    public const uint StillActive = 259;
    public const uint WaitObject0 = 0;

    static ConPtyNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(ConPtyNative).Assembly, ResolveConPty);
    }

    private static nint ResolveConPty(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, ConPtyDll, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return NativeLibrary.TryLoad(ConPtyDll, assembly, searchPath, out var handle)
            ? handle
            : NativeLibrary.Load(Kernel32);
    }

    // ------------------------------------------------------------- structs

    [StructLayout(LayoutKind.Sequential)]
    public struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoW
    {
        public uint cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoExW
    {
        public StartupInfoW StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    // -------------------------------------------------------- pseudoconsole

    [LibraryImport(ConPtyDll, EntryPoint = "CreatePseudoConsole")]
    public static partial int CreatePseudoConsole(Coord size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out nint phPC);

    [LibraryImport(ConPtyDll, EntryPoint = "ResizePseudoConsole")]
    public static partial int ResizePseudoConsole(nint hPC, Coord size);

    /// <summary>
    /// Drops the reference that keeps the console host alive, so it exits on its own once
    /// the last client disconnects. Not present in older in-box kernel32 builds.
    /// </summary>
    [LibraryImport(ConPtyDll, EntryPoint = "ReleasePseudoConsole")]
    public static partial int ReleasePseudoConsole(nint hPC);

    /// <summary>
    /// Closes the signal pipe, which the host treats like the console window being
    /// closed: attached clients receive CTRL_CLOSE_EVENT. Returns immediately.
    /// </summary>
    [LibraryImport(ConPtyDll, EntryPoint = "ClosePseudoConsole")]
    public static partial void ClosePseudoConsole(nint hPC);

    // --------------------------------------------------------------- pipes

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, nint lpPipeAttributes, uint nSize);

    // ------------------------------------------------------------ processes

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(nint lpAttributeList, uint dwAttributeCount, uint dwFlags, ref nuint lpSize);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(nint lpAttributeList, uint dwFlags, nuint attribute, nint lpValue, nuint cbSize, nint lpPreviousValue, nint lpReturnSize);

    [LibraryImport(Kernel32)]
    public static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

    [LibraryImport(Kernel32, EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcess(
        char* lpApplicationName,
        char* lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        char* lpEnvironment,
        char* lpCurrentDirectory,
        StartupInfoExW* lpStartupInfo,
        ProcessInformation* lpProcessInformation);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);
}
