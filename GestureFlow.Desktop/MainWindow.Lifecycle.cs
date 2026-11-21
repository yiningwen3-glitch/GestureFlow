using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GestureFlow.Desktop;

public partial class MainWindow
{
    private bool _exitRequested;
    private WindowState _restoreState = WindowState.Normal;
    private WelcomeWizard? _welcomeWizard;
    private CloseBehaviorDialog? _closeDialog;

    private bool ShowWelcomeWizard()
    {
        var wizard = new WelcomeWizard(_settings.SideButton, GlobalProfile.Configuration, _settings.Sensitivity.CrossingDistance()) { Owner = this };
        _welcomeWizard = wizard;
        try
        {
            if (wizard.ShowDialog() != true || _closed)
                return false;

            ChangeDesktopPreference(_settings with { WelcomeSeen = true, SideButton = wizard.SelectedSideButton });
            return wizard.WantsToEnable;
        }
        finally { _welcomeWizard = null; }
    }

    private void WelcomeGuideClick(object sender, RoutedEventArgs e)
    {
        if (ShowWelcomeWizard())
            _shell.NavigateTo(DesktopPage.Applications);
    }

    private void CloseBehaviorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded || _syncingSettings || CloseBehaviorPicker.SelectedIndex < 0)
            return;

        var action = (WindowCloseAction)CloseBehaviorPicker.SelectedIndex;
        ChangeDesktopPreference(_settings with { CloseAction = action });
    }

    private void CreateStartMenuShortcutClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ShellShortcuts.CreateStartMenu(Environment.GetFolderPath(Environment.SpecialFolder.Programs), DesktopExecutable);
            _shell.InputMessage = "已添加到当前用户的开始菜单。可搜索 GestureFlow。";
        }
        catch (Exception error) when (IsReadError(error))
        {
            _shell.InputMessage = $"无法添加开始菜单快捷方式 · {error.Message}";
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _exitRequested || _closed || _tray is null)
            return;

        var action = _settings.CloseAction;
        if (action == WindowCloseAction.Ask)
        {
            var dialog = new CloseBehaviorDialog { Owner = this };
            _closeDialog = dialog;
            bool accepted;
            try { accepted = dialog.ShowDialog() == true; }
            finally { _closeDialog = null; }
            if (_exitRequested || _closed)
                return;
            if (!accepted)
            {
                e.Cancel = true;
                return;
            }

            action = dialog.Choice;
            CloseBehaviorPicker.SelectedIndex = (int)action;
        }

        if (action == WindowCloseAction.Hide)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (_closed || _exitRequested)
                    return;

                _testWindow?.Close();
                Hide();
            }));
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized)
            _restoreState = WindowState;
    }

    internal void ShowMainWindow()
    {
        if (_closed || _exitRequested)
            return;

        if ((_closeDialog as Window ?? _welcomeWizard) is { } dialog)
        {
            if (dialog.WindowState == WindowState.Minimized)
                dialog.WindowState = WindowState.Normal;
            dialog.Activate();
            return;
        }

        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _restoreState;
        Activate();
    }

    private void ExitDesktop()
    {
        if (_closed || _exitRequested)
            return;

        _exitRequested = true;
        if (_closeDialog is { } dialog)
            dialog.Close();
        else
            Close();
    }
}
