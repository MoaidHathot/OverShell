using System.Runtime.InteropServices;
using System.Text;

namespace OverShell.App.Agents;

/// <summary>
/// Reads another process's current directory (DESIGN.md §12.14) the way ConEmu and
/// Process Explorer do: <c>NtQueryInformationProcess</c> for the PEB address, then
/// <c>ReadProcessMemory</c> for <c>PEB.ProcessParameters-&gt;CurrentDirectory.DosPath</c>.
/// The shell keeps that field current on every <c>cd</c>, so a tab learns where its shell
/// is without any prompt integration — the "working directory only updates when the shell
/// says so" limitation of §9. Needs <c>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ</c>,
/// which a user has on their own processes; an elevated shell under a non-elevated
/// OverShell refuses, and the read returns null. Both x64 and WOW64 (32-bit) targets are
/// handled, since <c>cmd.exe</c> from SysWOW64 is still around.
/// </summary>
internal static partial class ProcessCwd
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const int ProcessBasicInformationClass = 0;
    private const int ProcessWow64InformationClass = 26;

    // Offsets from the public PEB / RTL_USER_PROCESS_PARAMETERS layouts (winternl.h).
    private const int PebProcessParametersOffset64 = 0x20;
    private const int PebProcessParametersOffset32 = 0x10;
    private const int ParametersCurrentDirectoryOffset64 = 0x38;
    private const int ParametersCurrentDirectoryOffset32 = 0x24;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadProcessMemory(nint process, nint baseAddress, void* buffer, nint size, out nint bytesRead);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(nint process, int informationClass, void* information, uint length, out uint returnLength);

    /// <summary>The process's current directory, or null when it cannot be read (gone, protected, elevated, odd layout).</summary>
    public static unsafe string? Read(uint pid)
    {
        var process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            // A 32-bit target under a 64-bit OverShell: its real PEB is the 32-bit one.
            nint peb32 = 0;
            if (NtQueryInformationProcess(process, ProcessWow64InformationClass, &peb32, (uint)sizeof(nint), out _) == 0 && peb32 != 0)
            {
                return ReadCurrentDirectory32(process, peb32);
            }

            var info = default(ProcessBasicInformation);
            if (NtQueryInformationProcess(process, ProcessBasicInformationClass, &info, (uint)sizeof(ProcessBasicInformation), out _) != 0 || info.PebBaseAddress == 0)
            {
                return null;
            }

            return ReadCurrentDirectory64(process, info.PebBaseAddress);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static unsafe string? ReadCurrentDirectory64(nint process, nint peb)
    {
        if (!ReadPointer64(process, peb + PebProcessParametersOffset64, out var parameters) || parameters == 0)
        {
            return null;
        }

        // UNICODE_STRING { USHORT Length; USHORT MaximumLength; (pad) PWSTR Buffer; }
        var header = stackalloc byte[16];
        if (!ReadProcessMemory(process, parameters + ParametersCurrentDirectoryOffset64, header, 16, out var read) || read != 16)
        {
            return null;
        }

        var length = *(ushort*)header;
        var buffer = *(long*)(header + 8);
        return ReadString(process, (nint)buffer, length);
    }

    private static unsafe string? ReadCurrentDirectory32(nint process, nint peb32)
    {
        var pointer = stackalloc byte[4];
        if (!ReadProcessMemory(process, peb32 + PebProcessParametersOffset32, pointer, 4, out var read) || read != 4)
        {
            return null;
        }

        var parameters = *(uint*)pointer;
        if (parameters == 0)
        {
            return null;
        }

        // UNICODE_STRING32 { USHORT Length; USHORT MaximumLength; ULONG Buffer; }
        var header = stackalloc byte[8];
        if (!ReadProcessMemory(process, (nint)parameters + ParametersCurrentDirectoryOffset32, header, 8, out read) || read != 8)
        {
            return null;
        }

        var length = *(ushort*)header;
        var buffer = *(uint*)(header + 4);
        return ReadString(process, (nint)buffer, length);
    }

    private static unsafe bool ReadPointer64(nint process, nint address, out nint value)
    {
        long raw = 0;
        var ok = ReadProcessMemory(process, address, &raw, sizeof(long), out var read) && read == sizeof(long);
        value = (nint)raw;
        return ok;
    }

    private static unsafe string? ReadString(nint process, nint buffer, int lengthBytes)
    {
        // The DosPath is at most MAX_PATH-ish and always ends in a backslash; anything
        // longer or unreadable is treated as "don't know" rather than guessed at.
        if (buffer == 0 || lengthBytes <= 0 || lengthBytes > 32 * 1024)
        {
            return null;
        }

        var bytes = new byte[lengthBytes];
        fixed (byte* p = bytes)
        {
            if (!ReadProcessMemory(process, buffer, p, lengthBytes, out var read) || read != lengthBytes)
            {
                return null;
            }
        }

        var path = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        if (path.Length == 0)
        {
            return null;
        }

        // "C:\" keeps its backslash; "C:\Users\me\" loses it, matching what OSC 9;9 sends.
        return path.Length > 3 ? path.TrimEnd('\\') : path;
    }
}
