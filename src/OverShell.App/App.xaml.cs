using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace OverShell.App;

public partial class App : Application
{
    private static readonly string CrashLogPath =
        Path.Combine(Path.GetTempPath(), "overshell-crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Record("AppDomain", args.ExceptionObject as Exception);

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Record("Dispatcher", e.Exception);

        // Keep running: one bad tab shouldn't take the whole shell down.
        e.Handled = true;

        if (IsBenignTerminalTeardown(e.Exception) || _errorShown)
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

    private bool _errorShown;

    /// <summary>
    /// The hosted terminal control keeps delivering focus and key messages to a
    /// connection whose pseudoconsole has already been closed, and TermPTY throws rather
    /// than ignoring it. We guard against this with read-only mode, but the control can
    /// still win a race during teardown. Log it; never interrupt the user for it.
    /// </summary>
    private static bool IsBenignTerminalTeardown(Exception exception)
    {
        if (exception is not InvalidOperationException)
        {
            return false;
        }

        var declaringType = exception.TargetSite?.DeclaringType?.FullName;

        return declaringType?.Contains("TermPTY", StringComparison.Ordinal) == true
            || exception.Message.Contains("pseudoconsole", StringComparison.OrdinalIgnoreCase);
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
