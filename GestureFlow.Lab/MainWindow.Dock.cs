using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Lab.Simulation;
using GestureFlow.Profiles;
using GestureFlow.UI;

namespace GestureFlow.Lab;

public partial class MainWindow
{
    private readonly GestureProfileManager _dockProfiles = new();
    private readonly ProtectionPolicyService _dockProtection = new();
    private FloatingDockWindow? _labDock;
    private FloatingDockPreferences _labDockPreferences = new();
    private string LabDockPath => Path.Combine(_profileDirectory, "lab-dock.json");
    private VirtualApplicationScenario? DockApplication => DockLabApp.SelectedItem as VirtualApplicationScenario;

    private void InitializeDockLab()
    {
        VirtualApplicationScenario[] scenarios =
        [
            new("dock:desktop", "Windows 桌面", "explorer.exe", true),
            new("dock:solidworks", "SolidWorks", "sldworks.exe"),
            new("dock:vscode", "VS Code", "Code.exe", true),
            new("dock:photoshop", "Photoshop", "photoshop.exe")
        ];

        _dockProfiles.Create("模拟通用", select: true);
        foreach (var app in scenarios.Skip(1))
        {
            _dockProfiles.Create($"{app.DisplayName} · 常用", app.AppId, select: true);
            _dockProfiles.Create($"{app.DisplayName} · 8 方向", app.AppId, DefaultGestureProfiles.Create(GestureMode.Eight));
        }
        DockLabApp.ItemsSource = scenarios;
        DockLabApp.SelectedIndex = 0;
        _labDockPreferences = FloatingDockPreferences.Load(LabDockPath, out var recovered);
        if (recovered)
            DockLabResult.Text = "悬浮岛位置无法读取，已使用默认位置。";
        Loaded += (_, _) => ApplyLabDockVisibility();
    }

    private void DockLabAppChanged(object sender, SelectionChangedEventArgs e)
    {
        DockLabResult.Text = "";
        UpdateLabDock();
    }

    private ProtectionStatus DockLabProtection() => DockApplication?.StartsAuthorized != true
        ? ProtectionStatus.Disabled : _dockProtection.IsPaused ? ProtectionStatus.Paused : ProtectionStatus.Ready;

    private void UpdateLabDock()
    {
        if (DockApplication is not { } app)
            return;
        var state = FloatingDockState.ForApplication(app.DisplayName, app.AppId, _dockProfiles, DockLabProtection(),
            _dockProtection.IsPaused ? DockGestureMode.Paused : DockGestureMode.Running);
        DockLabStatus.Text = $"{state.Application} · {state.ProfileLabel} · {state.ModeLabel} · {state.ProtectionLabel}";
        _labDock?.UpdateState(state);
    }

    private void ToggleLabDock(object sender, RoutedEventArgs e) => SetLabDockVisible(!_labDockPreferences.Visible);

    private void SetLabDockVisible(bool visible)
    {
        SaveLabDockPreferences(_labDockPreferences with { Visible = visible });
        ApplyLabDockVisibility();
    }

    private void ApplyLabDockVisibility()
    {
        DockLabVisibility.Content = _labDockPreferences.Visible ? "隐藏悬浮岛" : "显示悬浮岛";
        if (!_labDockPreferences.Visible)
        {
            _labDock?.Hide();
            return;
        }
        if (_labDock is null)
        {
            _labDock = new FloatingDockWindow { Title = "GestureFlow Lab 悬浮岛" };
            _labDock.ProfileRequested += option =>
            {
                if (DockApplication?.AppId != option.AppId)
                    return;
                _dockProfiles.Select(option.ProfileId, option.AppId);
                UpdateLabDock();
                _labDock.Collapse();
            };
            _labDock.PauseRequested += () =>
            {
                _dockProtection.SetPaused(!_dockProtection.IsPaused);
                UpdateLabDock();
            };
            _labDock.SettingsRequested += () => { Tabs.SelectedItem = DockLabTab; Activate(); };
            _labDock.HideRequested += () => SetLabDockVisible(false);
            _labDock.PositionChanged += position => SaveLabDockPreferences(_labDockPreferences with { Position = position });
            _labDock.Closed += (_, _) => _labDock = null;
        }
        _labDock.ApplyPreferences(_labDockPreferences, resetExpansion: !_labDock.IsVisible);
        UpdateLabDock();
        if (!_labDock.IsVisible)
            _labDock.Show();
    }

    private void ResetLabDockPosition(object sender, RoutedEventArgs e)
    {
        SaveLabDockPreferences(_labDockPreferences with { Position = null });
        _labDock?.ResetPosition();
    }

    private void SaveLabDockPreferences(FloatingDockPreferences preferences)
    {
        _labDockPreferences = preferences;
        try { preferences.Save(LabDockPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            DockLabResult.Text = $"模拟悬浮岛设置保存失败 · {error.Message}";
        }
    }

    private void PreviewLabDockGesture(object sender, RoutedEventArgs e)
    {
        var profile = _dockProfiles.Resolve(DockApplication?.AppId, DockLabProtection());
        if (profile is null)
        {
            DockLabResult.Text = "虚拟输入放行 · 无命令";
            return;
        }
        var preview = SimulateDirection(profile, GestureDirection.North);
        DockLabResult.Text = preview is { } action
            ? $"模拟执行 · {action.DisplayName} · {GestureLabels.Shortcut(action.Shortcut)}"
            : "虚拟手势已完成 · 无命令";
    }
}
