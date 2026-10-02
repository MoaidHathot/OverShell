using System.Runtime.InteropServices;
using System.Text;

namespace OverShell.App;

/// <summary>
/// Asks Windows to start OverShell again after a restart or sign-out (DESIGN.md §12.13),
/// through <c>RegisterApplicationRestart</c> — what browsers use so they come back with
/// their tabs when "Automatically save my restartable apps and restart them when I sign
/// back in" is on. Only for restarts: the crash, hang and update flags are excluded, so a
/// crashing build cannot loop. Windows also refuses to restart a process that ran for less
/// than a minute. Opt-in through <c>session.restartWithWindows</c>.
/// </summary>
internal static class ApplicationRestart
{
    /// <summary>On the command line Windows uses, so the trace can tell an automatic start from a user's.</summary>
    public const string Marker = "--restarted-by-windows";

    private const int RestartNoCrash = 1;
    private const int RestartNoHang = 2;
    private const int RestartNoPatch = 4;

    /// <summary>Registers or unregisters to match <paramref name="enabled"/>. Returns what happened, for the trace.</summary>
    public static string Apply(bool enabled)
    {
        var registered = Registered();
        if (enabled && registered is null)
        {
            var hr = RegisterApplicationRestart(Marker, RestartNoCrash | RestartNoHang | RestartNoPatch);
            return hr == 0 ? $"registered ({Registered() ?? "?"})" : $"RegisterApplicationRestart failed: 0x{hr:X8}";
        }

        if (!enabled && registered is not null)
        {
            var hr = UnregisterApplicationRestart();
            return hr == 0 ? "unregistered" : $"UnregisterApplicationRestart failed: 0x{hr:X8}";
        }

        return enabled ? "already registered" : "not registered";
    }

    /// <summary>The registered command line and flags, read back from Windows; null when not registered.</summary>
    public static string? Registered()
    {
        uint size = 0;
        var hr = GetApplicationRestartSettings(GetCurrentProcess(), null, ref size, out _);
        if (hr == HResultNotFound)
        {
            return null;
        }

        var buffer = new StringBuilder((int)Math.Max(size, 1));
        hr = GetApplicationRestartSettings(GetCurrentProcess(), buffer, ref size, out var flags);
        return hr == 0 ? $"'{buffer}' flags=0x{flags:X}" : hr == HResultNotFound ? null : $"read-back failed: 0x{hr:X8}";
    }

    // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
    private const int HResultNotFound = unchecked((int)0x80070490);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    [DllImport("kernel32.dll")]
    private static extern int UnregisterApplicationRestart();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationRestartSettings(nint process, StringBuilder? commandLine, ref uint size, out uint flags);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
