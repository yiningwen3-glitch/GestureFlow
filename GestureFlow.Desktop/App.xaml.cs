using System.Windows;
using System.Windows.Threading;
using GestureFlow.UI;

namespace GestureFlow.Desktop;

public partial class App : Application
{
    private SingleInstanceLease? _instance;
    private readonly CancellationTokenSource _activationLifetime = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = SingleInstanceLease.TryAcquire("Desktop");
        var pipeName = DesktopActivation.CurrentSessionPipeName;
        if (_instance is null)
        {
            DesktopActivation.Request(pipeName);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherFailure;
        MainWindow = new MainWindow();
        _ = DesktopActivation.ListenAsync(pipeName,
            () => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                (MainWindow as MainWindow)?.ShowMainWindow())), _activationLifetime.Token);
        MainWindow.Show();
    }

    private void OnDispatcherFailure(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        (MainWindow as MainWindow)?.CloseRuntime();

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel)
            (MainWindow as MainWindow)?.CloseRuntime();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherFailure;
        try { (MainWindow as MainWindow)?.CloseRuntime(); }
        finally
        {
            _activationLifetime.Cancel();
            _activationLifetime.Dispose();
            _instance?.Dispose();
            base.OnExit(e);
        }
    }
}
