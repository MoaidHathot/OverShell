using System.Windows;

namespace DcompSpike;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0)
        {
            DcompSpike.MainWindow.Mode = e.Args[0];
        }

        base.OnStartup(e);
    }
}
