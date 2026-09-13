using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OverShell.Core.Integrations;

namespace OverShell.App;

public partial class App : Application
{
    private static readonly string CrashLogPath =
        Path.Combine(Path.GetTempPath(), "overshell-crash.log");

    private bool _errorShown;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(nint handle);

    private const uint AttachParentProcess = unchecked((uint)-1);
    private const int StdOutputHandle = -11;
    private const uint FileTypeChar = 0x0002;

    /// <summary>
    /// Where CLI output goes. A GUI process launched from a console has no console of its
    /// own, so the parent's is attached — but AttachConsole replaces the standard handles,
    /// which would lose a redirection like <c>OverShell integrations status &gt; out.txt</c>.
    /// So a redirected handle is used as-is, and the console is attached only otherwise.
    /// </summary>
    private static TextWriter OpenCliOutput()
    {
        var stdout = GetStdHandle(StdOutputHandle);
        if (stdout != 0 && stdout != -1 && GetFileType(stdout) != FileTypeChar)
        {
            var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(stdout, ownsHandle: false), FileAccess.Write);
            return new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        }

        _ = AttachConsole(AttachParentProcess);
        return Console.Out;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Record("AppDomain", args.ExceptionObject as Exception);

        base.OnStartup(e);

        // `OverShell integrations …` is a command-line tool run, not a window.
        if (e.Args.Length > 0 && e.Args[0].Equals("integrations", StringComparison.OrdinalIgnoreCase))
        {
            var code = RunIntegrationsCli(e.Args.Skip(1).ToArray());
            Shutdown(code);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    /// <summary>
    /// <c>integrations status</c> · <c>integrations install &lt;opencode|copilot|all&gt;</c> ·
    /// <c>integrations uninstall &lt;id&gt;</c>. Output goes to the console that launched us: a
    /// GUI process has none of its own, so the parent's is attached.
    /// </summary>
    private static int RunIntegrationsCli(string[] args)
    {
        var out_ = OpenCliOutput();

        try
        {
            var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            var ids = args.Length > 1 && !args[1].Equals("all", StringComparison.OrdinalIgnoreCase)
                ? [args[1]]
                : IntegrationInstaller.Ids;

            switch (verb)
            {
                case "status":
                    out_.WriteLine();
                    foreach (var id in ids)
                    {
                        Print(out_, IntegrationInstaller.Status(id));
                    }

                    return 0;

                case "install":
                    out_.WriteLine();
                    foreach (var id in ids)
                    {
                        Print(out_, IntegrationInstaller.Install(id));
                    }

                    out_.WriteLine("  Restart the harness inside an OverShell tab for the integration to load.");
                    return 0;

                case "uninstall":
                    out_.WriteLine();
                    foreach (var id in ids)
                    {
                        Print(out_, IntegrationInstaller.Uninstall(id));
                    }

                    return 0;

                default:
                    out_.WriteLine();
                    out_.WriteLine("usage: OverShell integrations status");
                    out_.WriteLine("       OverShell integrations install   <opencode|copilot|all>");
                    out_.WriteLine("       OverShell integrations uninstall <opencode|copilot|all>");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            out_.WriteLine();
            out_.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            out_.Flush();
        }
    }

    private static void Print(TextWriter out_, IntegrationStatus s) =>
        out_.WriteLine($"  {s.Id,-9} {(s.Installed ? s.Current ? "installed  " : "outdated   " : "absent     ")} {s.Path}{(s.HarnessFound ? string.Empty : "   (harness not on PATH)")}");

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Record("Dispatcher", e.Exception);

        // Keep running: one bad tab shouldn't take the whole shell down.
        e.Handled = true;

        if (_errorShown)
        {
            return;
        }

        _errorShown = true;

        MessageBox.Show(
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n"
          + $"Details written to:\n{CrashLogPath}\n\n"
          + "Further errors this session will be logged silently.",
            "OverShell",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void Record(string origin, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(
                CrashLogPath,
                $"[{DateTime.Now:O}] {origin}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing sensible to do if even logging fails.
        }
    }
}
