using System.IO;
using System.Windows;
using System.Windows.Controls;
using GestureFlow.Profiles;
using GestureFlow.Core.Protection;
using Microsoft.Win32;

namespace GestureFlow.Desktop;

public partial class MainWindow
{
    private readonly DesktopSettingsStore _settingsStore;
    private DesktopSettings _settings = null!;
    private bool _settingsLoaded;
    private bool _syncingSettings;

    private static string StartupDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    private static string DesktopExecutable => Path.Combine(AppContext.BaseDirectory, "GestureFlow.Desktop.exe");

    private void InitializeDesktopSettings()
    {
        _settings = _settingsStore.Load(out var recovered);
        _profiles = _settingsStore.LoadProfiles(out var migrationWarning);
        ProfileStatus.Text = migrationWarning ?? _profiles.LoadWarning;
        RefreshProfileScopes();
        ShowProfiles();
        PauseHotkeyPicker.ItemsSource = Hotkeys;
        PauseHotkeyPicker.DisplayMemberPath = nameof(HotkeyOption.Label);
        ApplyStoredSettings();
        LoadStartupOption();
        _settingsLoaded = true;
        if (recovered)
            _shell.InputMessage = "部分配置无法读取。已使用可读取的旧设置或默认值，原文件保留至下次保存。";
    }

    private void ApplyStoredSettings()
    {
        SyncSettingsPickers();
        ApplyDesktopTheme();
        ApplyGestureSettings();
    }

    private void SyncSettingsPickers()
    {
        _syncingSettings = true;
        try
        {
            PauseHotkeyPicker.SelectedIndex = _settings.PauseHotkey;
            CloseBehaviorPicker.SelectedIndex = (int)_settings.CloseAction;
            SideButtonPicker.SelectedValue = _settings.SideButton;
            UpdateTriggerLabels();
            SensitivityPicker.SelectedValue = _settings.Sensitivity;
            _shell.SelectedTheme = _shell.Themes.First(option => option.Theme == _settings.Theme);
        }
        finally { _syncingSettings = false; }
    }

    private void CommitDesktopSettings(DesktopSettings next)
    {
        _settingsStore.Save(next);
        _settings = next;
    }

    private void ChangeDesktopPreference(DesktopSettings next)
    {
        if (_settings == next)
            return;

        var previous = _settings;
        try { CommitDesktopSettings(next); }
        catch (Exception error) when (IsReadError(error) || error is ProfileValidationException)
        {
            SyncSettingsPickers();
            _shell.InputMessage = $"设置保存失败 · {error.Message}";
            return;
        }

        if (previous.SideButton != next.SideButton)
        {
            UpdateTriggerLabels();

            _desktopInput.Invalidate();
            _testWindow?.SetTrigger(next.SideButton);
            RefreshApplicationDetails();
            ShowForeground();
        }
        if (previous.Theme != next.Theme)
            ApplyDesktopTheme();
        if (previous.Sensitivity != next.Sensitivity)
            ApplyGestureSettings();
    }

    private void UpdateTriggerLabels()
    {
        var label = _settings.SideButton == GestureTrigger.MouseX2 ? "鼠标侧键 2" : "鼠标侧键 1";
        HomeTriggerText.Text = label;
        AppTriggerText.Text = $"触发键 · {label}（可在设置更改）";
    }

    private void ApplyGestureSettings()
    {
        HomeWheel.CancelRadius = _settings.Sensitivity.CenterRadius();
        HomeWheel.Show(GlobalProfile.Configuration);
        GestureConfiguration.SetSensitivity(_settings.Sensitivity);
        _desktopInput.SetGestureSettings(GlobalProfile.Configuration, _settings.Sensitivity.CrossingDistance());
        UpdateInputControls();
    }

    private void SideButtonChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsLoaded && !_syncingSettings && SideButtonPicker.SelectedValue is GestureTrigger trigger)
            ChangeDesktopPreference(_settings with { SideButton = trigger });
    }

    private void SensitivityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsLoaded && !_syncingSettings && SensitivityPicker.SelectedValue is WheelSensitivity sensitivity)
            ChangeDesktopPreference(_settings with { Sensitivity = sensitivity });
    }

    private void DesktopThemeChanged()
    {
        if (_settingsLoaded && !_syncingSettings && _shell.SelectedTheme is { } choice)
            ChangeDesktopPreference(_settings with { Theme = choice.Theme });
    }

    private void LoadStartupOption()
    {
        var directory = StartupDirectory;
        StartupCheckBox.IsEnabled = !string.IsNullOrWhiteSpace(directory);
        StartupCheckBox.IsChecked = StartupCheckBox.IsEnabled && ShellShortcuts.IsStartupEnabled(directory);
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        var enabled = StartupCheckBox.IsChecked == true;
        try
        {
            ShellShortcuts.SetStartup(StartupDirectory, DesktopExecutable, enabled);
            _shell.InputMessage = enabled ? "已添加当前用户的开机启动项。" : "已移除当前用户的开机启动项。";
        }
        catch (Exception error) when (IsReadError(error))
        {
            LoadStartupOption();
            _shell.InputMessage = $"无法更新开机启动 · {error.Message}";
        }
    }

    private void BackupSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "备份 GestureFlow 配置",
            Filter = "GestureFlow 配置备份 (*.json)|*.json",
            FileName = "GestureFlow-backup.json"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _settingsStore.Export(dialog.FileName, _settings with { Profile = GlobalProfile.Configuration });
            _shell.InputMessage = $"已备份已保存的配置 · {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception error) when (IsReadError(error) || error is ProfileValidationException)
        {
            _shell.InputMessage = $"备份失败 · {error.Message}";
        }
    }

    private void RestoreSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "恢复 GestureFlow 配置", Filter = "GestureFlow 配置备份 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var restored = _settingsStore.ReadBackup(dialog.FileName, _settings);
            var draft = GestureConfiguration.Model.HasChanges ? "未保存的手势草稿也会被替换。" : "";
            if (MessageBox.Show(this, $"恢复会应用备份中的设置、选用从备份创建的通用方案并关闭输入监听。{draft}继续？",
                "恢复 GestureFlow 配置", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes
                || _closed)
                return;

            RestoreDesktopSettings(restored);
            _shell.InputMessage = "已恢复配置。全局输入处于关闭状态。";
        }
        catch (Exception error) when (IsReadError(error) || error is ProfileValidationException)
        {
            _shell.InputMessage = $"恢复失败 · {error.Message}";
        }
    }

    private void RestoreDesktopSettings(DesktopSettings restored)
    {
        StopDesktopInput();
        var previous = _settings;
        CommitDesktopSettings(restored);
        try { _profiles.Create("从备份恢复的通用方案", configuration: restored.Profile, select: true); }
        catch
        {
            CommitDesktopSettings(previous);
            throw;
        }
        ShowProfiles();
        ApplyStoredSettings();
    }
}
