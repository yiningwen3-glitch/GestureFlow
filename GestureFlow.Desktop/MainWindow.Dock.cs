using System.Windows;
using System.Windows.Controls;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop.Applications;
using GestureFlow.UI;
using Forms = System.Windows.Forms;

namespace GestureFlow.Desktop;

public partial class MainWindow
{
    private sealed record DockOpacityChoice(double Value)
    {
        public string Label => $"{Value:P0}";
    }

    private readonly string _dockPreferencesPath;
    private FloatingDockPreferences _dockPreferences = new();
    private FloatingDockWindow? _dock;
    private Forms.ToolStripMenuItem? _trayDock;
    private bool _syncingDockSettings;

    private void InitializeFloatingDock()
    {
        _dockPreferences = FloatingDockPreferences.Load(_dockPreferencesPath, out var recovered);
        var values = new[] { FloatingDockPreferences.MinimumOpacity, 0.60, 0.75, 0.90, 1.0 };
        DockOpacitySetting.ItemsSource = values.Append(_dockPreferences.Opacity).Distinct().Order()
            .Select(value => new DockOpacityChoice(value)).ToArray();
        ApplyDockPreferences();
        if (recovered)
            DockSettingsStatus.Text = "无法读取悬浮岛偏好，已采用默认设置。";
    }

    private void ApplyDockPreferences()
    {
        _syncingDockSettings = true;
        try
        {
            DockVisibleSetting.IsChecked = _dockPreferences.Visible;
            DockTopmostSetting.IsChecked = _dockPreferences.AlwaysOnTop;
            DockCollapsedSetting.IsChecked = _dockPreferences.StartCollapsed;
            DockAutoCollapseSetting.IsChecked = _dockPreferences.AutoCollapse;
            DockOpacitySetting.SelectedValue = _dockPreferences.Opacity;
        }
        finally { _syncingDockSettings = false; }

        if (_trayDock is not null)
            _trayDock.Text = _dockPreferences.Visible ? "隐藏悬浮岛" : "显示悬浮岛";
        if (!_dockPreferences.Visible)
        {
            _dock?.Hide();
            return;
        }

        if (_dock is null)
        {
            _dock = new FloatingDockWindow();
            _dock.ProfileRequested += SelectDockProfile;
            _dock.PauseRequested += () =>
            {
                if (_desktopInput.IsRunning && !_desktopInput.IsTestMode)
                    SetPaused(!_protection.IsPaused);
            };
            _dock.SettingsRequested += () =>
            {
                _shell.NavigateTo(DesktopPage.Settings);
                ShowMainWindow();
            };
            _dock.HideRequested += () => SetDockVisible(false);
            _dock.PositionChanged += position => SaveDockPreferences(_dockPreferences with { Position = position });
            _dock.InteractionCompleted += () => _ = CheckForegroundAsync();
            _dock.Closed += (_, _) => _dock = null;
        }

        var show = !_dock.IsVisible;
        _dock.ApplyPreferences(_dockPreferences, resetExpansion: show);
        if (show)
        {
            ShowForeground();
            _dock.Show();
            _ = CheckForegroundAsync();
        }
    }

    private void SetDockVisible(bool visible) => SaveDockPreferences(_dockPreferences with { Visible = visible });

    private void FloatingDockSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingDockSettings || _closed)
            return;
        SaveDockPreferences(_dockPreferences with
        {
            Visible = DockVisibleSetting.IsChecked == true,
            AlwaysOnTop = DockTopmostSetting.IsChecked == true,
            StartCollapsed = DockCollapsedSetting.IsChecked == true,
            AutoCollapse = DockAutoCollapseSetting.IsChecked == true
        });
    }

    private void FloatingDockOpacityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingDockSettings && !_closed && DockOpacitySetting.SelectedValue is double value)
            SaveDockPreferences(_dockPreferences with { Opacity = value });
    }

    private void ResetFloatingDockPosition(object sender, RoutedEventArgs e)
    {
        SaveDockPreferences(_dockPreferences with { Position = null });
        _dock?.ResetPosition();
    }

    private void SaveDockPreferences(FloatingDockPreferences preferences)
    {
        if (_closed)
            return;
        _dockPreferences = preferences;
        ApplyDockPreferences();
        try
        {
            preferences.Save(_dockPreferencesPath);
            DockSettingsStatus.Text = "";
            _dock?.ShowFeedback(null);
        }
        catch (Exception error) when (IsReadError(error))
        {
            var message = $"悬浮岛偏好保存失败 · {error.Message}";
            DockSettingsStatus.Text = message;
            _dock?.ShowFeedback(message);
        }
    }

    private void UpdateDockState(string application, InstalledApplication? match, ProtectionStatus protection,
        bool highRisk = false, bool inputRestricted = false)
    {
        if (_dock is null)
            return;
        var mode = !_desktopInput.IsRunning ? DockGestureMode.Stopped
            : _desktopInput.IsTestMode ? DockGestureMode.TestOnly
            : _protection.IsPaused ? DockGestureMode.Paused : DockGestureMode.Running;
        var appId = match is { IdentificationStatus: IdentificationStatus.Matched } ? match.AppId : null;
        _dock.UpdateState(FloatingDockState.ForApplication(application, appId, _profiles, protection, mode, highRisk, inputRestricted));
    }

    private void SelectDockProfile(DockProfileOption option)
    {
        var target = _businessForeground.ForProfileSelection(ForegroundApplicationService.ForegroundWindow(),
            ForegroundApplicationService.IsLive);
        if (_closed || target is null || ResolveForeground(target).Application is not { IdentificationStatus: IdentificationStatus.Matched } app
            || !string.Equals(app.AppId, option.AppId, StringComparison.OrdinalIgnoreCase))
        {
            _dock?.ShowFeedback("前台应用已变化，请重新选择方案。");
            return;
        }

        try
        {
            if (_profiles.Selected(option.AppId)?.ProfileId != option.ProfileId)
            {
                _profiles.Select(option.ProfileId, option.AppId);
                UpdateSelectedProfileText();
                ApplyGestureSettings();
                ShowForeground();
            }
            _dock?.ShowFeedback(null);
            _dock?.Collapse();
        }
        catch (Exception error) when (IsReadError(error) || error is InvalidOperationException)
        {
            _dock?.ShowFeedback($"切换方案失败 · {error.Message}");
        }
    }

    private void RefreshDockEnvironment()
    {
        _dock?.UpdatePlacement();
        _ = CheckForegroundAsync();
    }
}
