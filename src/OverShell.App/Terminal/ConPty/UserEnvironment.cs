using System.Runtime.InteropServices;

namespace OverShell.App.Terminal.ConPty;

/// <summary>
/// The environment a fresh logon would give this user, built from the registry (system
/// and user variables, the volatile per-session ones, the ProgramFiles family) rather than
/// inherited from whatever started OverShell (DESIGN.md §12.15). Windows Terminal does
/// the same for every new tab (<c>compatibility.reloadEnvironmentVariables</c>), which is
/// why a tool installed an hour ago is on PATH in a new Terminal tab but not in a window
/// that inherited its launcher's stale block - Explorer refreshes its own environment on
/// <c>WM_SETTINGCHANGE</c>, a browser or a long-running app does not, and a process started
/// through a URL handler gets whatever that app had.
/// <para>
/// <c>CreateEnvironmentBlock</c> is the userenv API the logon path uses; Terminal's
/// <c>til::env::regenerate</c> re-implements it. Same result: PATH is the system value
/// followed by the user value, <c>%X%</c> references are expanded against the block being
/// built, and variables that live only in the launcher's process are not carried over.
/// </para>
/// </summary>
internal static class UserEnvironment
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(nint environment);

    /// <summary>
    /// The regenerated environment, case-insensitively keyed; null when Windows refused,
    /// in which case the caller inherits as before. Costs well under a millisecond.
    /// </summary>
    public static Dictionary<string, string>? FromRegistry()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out var token))
        {
            return null;
        }

        try
        {
            if (!CreateEnvironmentBlock(out var block, token, inherit: false))
            {
                return null;
            }

            try
            {
                return Parse(block);
            }
            finally
            {
                _ = DestroyEnvironmentBlock(block);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    /// <summary>A double-null-terminated block of <c>name=value</c> strings into a map.</summary>
    private static unsafe Dictionary<string, string> Parse(nint block)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cursor = (char*)block;
        while (*cursor != '\0')
        {
            var entry = new string(cursor);
            cursor += entry.Length + 1;

            // A leading '=' belongs to the hidden per-drive entries (=C:=C:\dir); skip those.
            var separator = entry.IndexOf('=', 1);
            if (separator > 0)
            {
                variables[entry[..separator]] = entry[(separator + 1)..];
            }
        }

        return variables;
    }
}
