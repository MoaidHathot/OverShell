using System.Diagnostics;

namespace OverShell.App;

/// <summary>
/// Running as a .NET tool (DESIGN.md §13). `dotnet tool install` writes a <c>.cmd</c> shim
/// that runs <c>OverShell.exe</c> from the tool store and — being a batch file — waits for
/// it; <c>dnx OverShell</c> waits too. For a command-line verb that is right. For the
/// window it would hold the user's terminal until they close OverShell, so the first
/// process starts a detached twin and exits, and the wrapper returns at once.
/// <para>
/// The twin is started through ShellExecute, which hands down <em>no</em> inheritable
/// handles: with a plain CreateProcess the twin would keep the wrapper's stdout pipe
/// open, and anything capturing that output (<c>overshell | Out-Null</c>, a script) would
/// wait until the window closed — measured at three minutes before this was understood.
/// </para>
/// </summary>
internal static class ToolLaunch
{
    /// <summary>Passed to the twin so it does not detach again; never shown, never a URL.</summary>
    public const string DetachedMarker = "--detached";

    /// <summary>
    /// True when this process was started through a tool wrapper: its image lives in a
    /// tool store (<c>\.store\overshell\</c>, from <c>dotnet tool install</c>) or in the
    /// NuGet packages folder (<c>\.nuget\packages\overshell\</c>, from <c>dnx</c>), and
    /// it is not already the twin.
    /// </summary>
    public static bool ShouldDetach(IReadOnlyList<string> args)
    {
        if (args.Any(a => a.Equals(DetachedMarker, StringComparison.OrdinalIgnoreCase) || a.Equals("--no-detach", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var path = Environment.ProcessPath;
        return path is not null &&
               (path.Contains(@"\.store\overshell\", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(@"\.nuget\packages\overshell\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Starts the twin with the same arguments plus the marker. Returns false when it could not be started.</summary>
    public static bool Detach(IReadOnlyList<string> args)
    {
        var path = Environment.ProcessPath;
        if (path is null)
        {
            return false;
        }

        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.ArgumentList.Add(DetachedMarker);

        try
        {
            using var twin = Process.Start(info);
            return twin is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Arguments without the marker, for everything downstream.</summary>
    public static string[] Strip(IReadOnlyList<string> args) =>
        args.Where(a => !a.Equals(DetachedMarker, StringComparison.OrdinalIgnoreCase) && !a.Equals("--no-detach", StringComparison.OrdinalIgnoreCase)).ToArray();
}
