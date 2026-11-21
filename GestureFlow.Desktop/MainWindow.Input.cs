using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop.Applications;
using GestureFlow.Desktop.Input;
using GestureFlow.Profiles;
using Forms = System.Windows.Forms;

namespace GestureFlow.Desktop;

public partial class MainWindow
{
    private sealed record HotkeyOption(string Label, uint Modifiers, uint Key);

    private static readonly HotkeyOption[] Hotkeys =
    [
        new("Ctrl + Shift + F10", 0x2 | 0x4, 0x79),
        new("Ctrl + Alt + F11", 0x2 | 0x1, 0x7A),
        new("Alt + Shift + F9", 0x1 | 0x4, 0x78)
    ];

    private const int PauseHotkeyId = 0x672;
    private const string DesktopInputTarget = "windows-desktop";
    private readonly DesktopInputController _desktopInput;
    private bool _globalInputEnabled;
    private HwndSource? _inputHwnd;
    private bool _hotkeyRegistered;
    private uint _taskbarCreated;
    private InputTestWindow? _testWindow;
    private (bool Running, bool Global, bool Paused) _inputBeforeTest;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayPause;
    private Forms.ToolStripMenuItem? _trayStop;

    private void InitializeDesktopInput()
    {
        _inputHwnd = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _inputHwnd?.AddHook(InputWindowMessage);
        _taskbarCreated = WindowsInputNative.RegisterWindowMessage("TaskbarCreated");
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 GestureFlow", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowMainWindow)));
        _trayDock = (Forms.ToolStripMenuItem)menu.Items.Add("显示悬浮岛", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => SetDockVisible(!_dockPreferences.Visible))));
        _trayPause = (Forms.ToolStripMenuItem)menu.Items.Add("紧急暂停", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => SetPaused(!_protection.IsPaused))));
        _trayStop = (Forms.ToolStripMenuItem)menu.Items.Add("关闭手势监听", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(StopDesktopInput)));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(new Action(ExitDesktop)));
        var iconUri = new Uri("pack://application:,,,/GestureFlow.UI;component/Assets/GestureFlow.ico");
        using (var stream = Application.GetResourceStream(iconUri).Stream)
            _trayIcon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        _tray = new Forms.NotifyIcon { Text = "GestureFlow", Icon = _trayIcon, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowMainWindow));
        UpdateInputControls();
    }

    private void DisposeDesktopInput()
    {
        _desktopInput.Dispose();
        ReleaseHotkey();
        _inputHwnd?.RemoveHook(InputWindowMessage);
        _testWindow?.Close();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
    }

    private void UpdateInputControls()
    {
        _shell.UpdateInput(_desktopInput.IsRunning, _protection.IsPaused, _desktopInput.IsTestMode, _globalInputEnabled);
        PauseHotkeyPicker.IsEnabled = !_desktopInput.IsRunning;
        EnableAppInputButton.Visibility = SelectedApplication?.GestureEnabled == true && !_globalInputEnabled && !_desktopInput.IsTestMode
            ? Visibility.Visible : Visibility.Collapsed;
        if (_trayPause is not null)
        {
            _trayPause.Text = _protection.IsPaused ? "恢复手势" : "紧急暂停";
            _trayPause.Enabled = _desktopInput.IsRunning;
        }
        if (_trayStop is not null)
            _trayStop.Enabled = _desktopInput.IsRunning;
    }

    private (string AppId, GestureProfile Profile)? AuthorizeInput(RunningApplication running)
    {
        if (_closed || _protection.IsPaused)
            return null;
        if (running.WindowKind == ForegroundWindowKind.ExplorerDesktop)
            return running.InstallationStamp is not null
                ? (DesktopInputTarget, GlobalProfile.Configuration) : null;
        if (!_globalInputEnabled || _refreshing || !_policiesReadable)
            return null;

        var target = ResolveForeground(running);
        return target is { Application: { } match, Authorization: not null, Profile: { } profile }
            ? (match.AppId, profile.Configuration) : null;
    }

    private void GlobalInputClick(object sender, RoutedEventArgs e)
    {
        if (!_desktopInput.IsRunning)
            StartDesktopInput();
        else if (_desktopInput.IsTestMode)
            StopDesktopInput();
        else if (_globalInputEnabled)
        {
            _globalInputEnabled = false;
            _desktopInput.Invalidate();
            UpdateInputControls();
            ReportInput(_shell.InputState);
            ShowForeground();
        }
        else
            RequestGlobalInput();
    }

    private void RequestGlobalInput()
    {
        if (_closed || _globalInputEnabled || _desktopInput.IsTestMode)
            return;
        if (_refreshing || !_policiesReadable)
        {
            _shell.InputMessage = "请先完成应用清单的读取。";
            return;
        }
        if (!_desktopInput.IsRunning)
            StartDesktopInput();
        if (!_desktopInput.IsRunning)
            return;

        _globalInputEnabled = true;
        _desktopInput.Invalidate();
        UpdateInputControls();
        ReportInput(_shell.InputState);
        ShowForeground();
    }

    private void StartDesktopInput(nint testWindow = 0)
    {
        if (_desktopInput.IsRunning || _closed)
            return;
        try
        {
            if (_inputHwnd is null || PauseHotkeyPicker.SelectedItem is not HotkeyOption selected
                || !WindowsInputNative.RegisterHotKey(_inputHwnd.Handle, PauseHotkeyId, selected.Modifiers | 0x4000, selected.Key))
                throw new InvalidOperationException("无法注册暂停热键，请换一个组合后再试。");

            _hotkeyRegistered = true;
            SetPaused(false);
            _desktopInput.Start(testWindow);
            _shell.InputMessage = testWindow != 0
                ? $"仅测试窗口已开启 · 暂停 {selected.Label}"
                : $"桌面手势已开启 · 暂停 {selected.Label}";
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            _desktopInput.Stop();
            ReleaseHotkey();
            _shell.InputMessage = $"手势监听已关闭 · {error.GetBaseException().Message}";
        }
        UpdateInputControls();
        ShowForeground();
        _testWindow?.SetRunning(_desktopInput.IsTestMode && _desktopInput.IsRunning);
    }

    private void StopDesktopInput()
    {
        _desktopInput.Stop();
        if (_protection.IsPaused)
            SetPaused(false);
        DesktopInputStopped();
        _shell.InputMessage = "手势监听已关闭";
    }

    private void DesktopInputStopped()
    {
        _globalInputEnabled = false;
        ReleaseHotkey();
        UpdateInputControls();
        ShowForeground();
        _testWindow?.SetRunning(false);
    }

    private void ReleaseHotkey()
    {
        if (_hotkeyRegistered && _inputHwnd is not null)
            WindowsInputNative.UnregisterHotKey(_inputHwnd.Handle, PauseHotkeyId);
        _hotkeyRegistered = false;
    }

    private nint InputWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_closed)
            return 0;

        switch (DesktopRuntimeSettings.SystemEvent(message, wParam, _taskbarCreated))
        {
            case RuntimeSystemEvent.PowerTransition:
            case RuntimeSystemEvent.ShellRestart:
                var wasRunning = _desktopInput.IsRunning;
                StopDesktopInput();
                if (wasRunning)
                    _shell.InputMessage = "系统会话或资源管理器状态已变化。监听已关闭，可手动重新开启。";
                RefreshDockEnvironment();
                break;
            case RuntimeSystemEvent.DisplayChanged:
                _desktopInput.Invalidate();
                RefreshDockEnvironment();
                break;
        }

        if (message == WindowsInputNative.WmHotkey && wParam == PauseHotkeyId && _desktopInput.IsRunning)
        {
            SetPaused(true);
            ReportInput("已通过紧急热键暂停。");
            handled = true;
        }
        return 0;
    }

    private void ReportInput(string message)
    {
        _shell.InputMessage = message;
        if (_desktopInput.IsTestMode)
            _testWindow?.Report(message);
    }

    private void OpenInputTestClick(object sender, RoutedEventArgs e)
    {
        if (_testWindow is null)
        {
            var window = new InputTestWindow(() =>
            {
                _inputBeforeTest = (_desktopInput.IsRunning, _globalInputEnabled, _protection.IsPaused);
                if (_desktopInput.IsRunning)
                    StopDesktopInput();
                StartDesktopInput(new WindowInteropHelper(_testWindow!).Handle);
                if (!_desktopInput.IsRunning)
                {
                    _testWindow!.Report(_shell.InputMessage);
                    EndInputTest();
                }
            }, EndInputTest, () => SetPaused(!_protection.IsPaused)) { Owner = this };
            _testWindow = window;
            window.SetTrigger(_settings.SideButton);
            window.Closed += (_, _) =>
            {
                _testWindow = null;
                if (_desktopInput.IsTestMode && !_closed)
                    EndInputTest();
            };
            window.Show();
        }
        _testWindow.Activate();
    }

    private void EndInputTest()
    {
        var previous = _inputBeforeTest;
        _inputBeforeTest = default;
        StopDesktopInput();
        if (!previous.Running || _closed)
            return;
        StartDesktopInput();
        if (!_desktopInput.IsRunning)
            return;
        _globalInputEnabled = previous.Global;
        SetPaused(previous.Paused);
        UpdateInputControls();
        ReportInput(_shell.InputState);
        ShowForeground();
    }

    private void PauseHotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded || _syncingSettings || PauseHotkeyPicker.SelectedIndex < 0 || _desktopInput.IsRunning)
            return;

        ChangeDesktopPreference(_settings with { PauseHotkey = PauseHotkeyPicker.SelectedIndex });
    }

    private void ApplyDesktopTheme()
    {
#pragma warning disable WPF0001 // WPF Fluent theme API in .NET 10.
        Application.Current.ThemeMode = _settings.Theme switch
        {
            DesktopTheme.Light => ThemeMode.Light,
            DesktopTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System
        };
#pragma warning restore WPF0001
        _desktopInput.Invalidate();
    }

    private async void ResetProtectionClick(object sender, RoutedEventArgs e)
    {
        if (_refreshing || MessageBox.Show(this, "关闭监听并停用所有应用的侧键笔势。笔势方案和应用清单会保留。继续？",
            "停用所有应用笔势", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        StopDesktopInput();
        _policiesReadable = false;
        _authorizations = new Dictionary<string, GestureAuthorization>();
        try
        {
            _store.ResetProtection();
            _shell.InputMessage = "已停用所有应用的侧键笔势。可在应用管理中重新启用。";
        }
        catch (Exception error) when (IsReadError(error))
        {
            _shell.InputMessage = $"停用应用笔势失败 · {error.Message}";
            SetButtonStates();
            return;
        }

        await RefreshAsync();
    }

    private void OpenLabClick(object sender, RoutedEventArgs e)
    {
        if (OpenLab() is { } message)
            _shell.InputMessage = message;
    }

    private static string? OpenLab()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        var lab = Path.Combine(current.Parent?.FullName ?? current.FullName, "Lab", "GestureFlow.Lab.exe");
        for (var root = current; !File.Exists(lab) && root is not null; root = root.Parent)
        {
            if (File.Exists(Path.Combine(root.FullName, "GestureFlow.sln")))
                lab = Path.Combine(root.FullName, "GestureFlow.Lab", "bin", current.Parent?.Name ?? "Release",
                    "net10.0-windows", "GestureFlow.Lab.exe");
        }

        try
        {
            if (!File.Exists(lab))
                throw new FileNotFoundException("请保留便携包中的 Lab 目录，或先构建模拟器项目。");
            Process.Start(new ProcessStartInfo(lab) { WorkingDirectory = Path.GetDirectoryName(lab)! })?.Dispose();
            return null;
        }
        catch (Exception error) when (IsReadError(error))
        {
            return $"无法打开 Lab · {error.Message}";
        }
    }
}
