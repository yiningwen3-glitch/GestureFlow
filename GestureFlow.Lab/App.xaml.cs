using System.Windows;
using GestureFlow.UI;

namespace GestureFlow.Lab;

public partial class App : Application
{
    private SingleInstanceLease? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = SingleInstanceLease.TryAcquire("Lab");
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
