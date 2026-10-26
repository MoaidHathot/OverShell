using System.Runtime.ExceptionServices;

namespace OverShell.Tests;

internal static class Sta
{
    private static readonly Lazy<System.Windows.Threading.Dispatcher> WpfDispatcher = new(() =>
    {
        var ready = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // Load the shipped resources without running the product's OnStartup,
                // which would launch sessions or perform single-instance handoff.
                var application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                foreach (var name in new[] { "Palette", "Icons", "Controls" })
                    application.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/OverShell;component/Theme/{name}.xaml", UriKind.Absolute),
                    });
                application.Resources["IsNotNull"] = new OverShell.App.Chrome.IsNotNullConverter();
                ready.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception e) { ready.TrySetException(e); }
        }) { IsBackground = true, Name = "OverShell WPF tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    });

    public static void WpfRun(Func<Task> action) =>
        WpfDispatcher.Value.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("STA test did not complete.");
        }

        failure?.Throw();
    }
}
