using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GestureFlow.Core;
using GestureFlow.Lab.Simulation;
using GestureFlow.Profiles;
using GestureFlow.UI;
using Microsoft.Win32;

namespace GestureFlow.Lab;

public partial class MainWindow
{
    private const string ShortcutHelp = "点击快捷键按钮后按下组合键。Esc 取消录制。";

    private sealed class BindingRow(GestureBinding binding) : INotifyPropertyChanged
    {
        public GestureDirection Direction { get; } = binding.Direction;
        public string DirectionLabel => GestureLabels.Direction(Direction);
        public string DisplayName { get; private set; } = binding.DisplayName;
        public string ShortcutLabel { get; private set; } = GestureLabels.Shortcut(binding.Shortcut);
        public string NameFieldLabel => $"{DirectionLabel}的显示名称";
        public string ShortcutFieldLabel => $"录制{DirectionLabel}的快捷键";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetNameDraft(string name)
        {
            if (DisplayName == name)
                return;

            DisplayName = name;
            PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        }

        public void SetShortcutLabel(string label)
        {
            ShortcutLabel = label;
            PropertyChanged?.Invoke(this, new(nameof(ShortcutLabel)));
        }
    }

    private void RefreshSettings()
    {
        StopShortcutRecording();
        _refreshingSettings = true;
        try
        {
            _invalidNames.Clear();
            ModePicker.SelectedIndex = _editor.Profile.Mode == GestureMode.Eight ? 1 : 0;
            BindingRows.ItemsSource = _editor.Profile.Bindings.Select(binding => new BindingRow(binding)).ToArray();
            SettingsStatus.Text = ShortcutHelp;
            RefreshProfile();
            RefreshControls();
        }
        finally
        {
            _refreshingSettings = false;
        }
    }

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshingSettings)
            return;
        if (_invalidNames.Count > 0)
        {
            _refreshingSettings = true;
            ModePicker.SelectedIndex = _editor.Profile.Mode == GestureMode.Eight ? 1 : 0;
            _refreshingSettings = false;
            SettingsStatus.Text = "请先填写空白的显示名称。";
            return;
        }

        _editor.ChangeMode(ModePicker.SelectedIndex == 1 ? GestureMode.Eight : GestureMode.Four);
        RefreshSettings();
    }

    private void BindingNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshingSettings || sender is not TextBox { IsLoaded: true, DataContext: BindingRow row } box)
            return;

        var direction = row.Direction;
        row.SetNameDraft(box.Text);
        if (_editor.SetName(direction, box.Text))
        {
            _invalidNames.Remove(direction);
            RefreshProfile();
        }
        else
            _invalidNames.Add(direction);

        RefreshControls();
        SettingsStatus.Text = _invalidNames.Count == 0 ? ShortcutHelp : "显示名称不能为空。轮盘会保留上一个有效名称。";
    }

    private void TypeShortcutClick(object sender, RoutedEventArgs e)
    {
        StopShortcutRecording();
        if (sender is Button { DataContext: BindingRow row }
            && ShortcutTextDialog.Edit(this, row.ShortcutLabel) is { } value && _editor.SetShortcut(row.Direction, value))
        {
            RefreshSettings();
            SettingsStatus.Text = $"已设置 {GestureLabels.Shortcut(value)}。";
        }
    }

    private void RecordShortcutClick(object sender, RoutedEventArgs e)
    {
        StopShortcutRecording();
        if (sender is not Button { DataContext: BindingRow row } button)
            return;

        button.Focus();
        _shortcutButton = button;
        _shortcutChord = new();
        row.SetShortcutLabel("按下组合键…");
        SettingsStatus.Text = "按住组合键，松开所有主键后确认。只按 Esc 可取消。";
    }

    private void ShortcutFocusLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _shortcutButton))
            StopShortcutRecording();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_shortcutChord is not { } chord)
            return;

        e.Handled = true;
        var key = ShortcutRecorder.GetKey(e);
        var modifiers = Keyboard.Modifiers;
        if (e.IsRepeat || ShortcutRecorder.IsModifier(key))
            return;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopShortcutRecording();
            SettingsStatus.Text = "已取消快捷键录制。";
            return;
        }

        SettingsStatus.Text = chord.Press(key, modifiers)
            ? $"正在录制 {GestureLabels.Shortcut(chord.Preview!)}。松开主键后确认。"
            : "此按键无法录制。请尝试另一个主键，或按 Esc 取消。";
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_shortcutButton?.DataContext is not BindingRow row || _shortcutChord is not { } chord)
            return;

        e.Handled = true;
        if (chord.Release(ShortcutRecorder.GetKey(e)) is not { } shortcut || !_editor.SetShortcut(row.Direction, shortcut))
            return;

        StopShortcutRecording();
        SettingsStatus.Text = $"已录制 {GestureLabels.Shortcut(shortcut)}。";
        RefreshProfile();
    }

    private void StopShortcutRecording()
    {
        if (_shortcutButton?.DataContext is BindingRow row)
        {
            var binding = _editor.Profile.Bindings.FirstOrDefault(item => item.Direction == row.Direction);
            if (binding is not null)
                row.SetShortcutLabel(GestureLabels.Shortcut(binding.Shortcut));
            SettingsStatus.Text = ShortcutHelp;
        }

        _shortcutButton = null;
        _shortcutChord = null;
    }

    private void RestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        _editor.RestoreDefaults();
        RefreshSettings();
        SettingsStatus.Text = "已恢复当前模式的默认配置。";
    }

    private void SaveProfileClick(object sender, RoutedEventArgs e)
    {
        if (_invalidNames.Count > 0)
            return;

        SaveJson("保存手势配置", "gestureflow-profile.json", () => GestureProfileJson.Serialize(_editor.Profile),
            SettingsStatus, "已保存配置");
    }

    private void LoadProfileClick(object sender, RoutedEventArgs e) =>
        OpenJson("加载手势配置", json =>
        {
            _editor.Load(GestureProfileJson.Deserialize(json));
            RefreshSettings();
        }, SettingsStatus, "已加载配置");

    private void SaveCaseClick(object sender, RoutedEventArgs e)
    {
        if (_testCase is { } testCase)
            SaveJson("保存虚拟测试用例", "gestureflow-case.json", () => GestureTestCaseJson.Serialize(testCase),
                CaseStatus, "已保存用例");
    }

    private void LoadCaseClick(object sender, RoutedEventArgs e) =>
        OpenJson("打开虚拟测试用例", json =>
        {
            _testCase = GestureTestCaseJson.Deserialize(json);
            RefreshControls();
        }, CaseStatus, "已载入用例");

    private void SaveJson(string title, string fileName, Func<string> serialize, TextBlock feedback, string success)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = "JSON (*.json)|*.json", FileName = fileName, DefaultExt = ".json" };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var json = serialize();
            var temporaryFile = $"{dialog.FileName}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryFile, json);
                File.Move(temporaryFile, dialog.FileName, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryFile);
            }
            feedback.Text = $"{success} · {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception error) when (IsFileError(error))
        {
            feedback.Text = $"保存失败 · {error.Message}";
        }
    }

    private void OpenJson(string title, Action<string> load, TextBlock feedback, string success)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            load(File.ReadAllText(dialog.FileName));
            feedback.Text = $"{success} · {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception error) when (IsFileError(error))
        {
            feedback.Text = $"加载失败 · {error.Message}";
        }
    }

    private static bool IsFileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or ProfileValidationException;
}
