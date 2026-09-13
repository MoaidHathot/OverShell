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

    private bool _errorShown;

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
