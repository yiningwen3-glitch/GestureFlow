using System.Windows;
using System.Windows.Controls;
using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Lab.Simulation;
using GestureFlow.UI;

namespace GestureFlow.Lab;

public partial class MainWindow
{
    private readonly HashSet<string> _virtualEnabled = new(StringComparer.Ordinal);
    private bool _virtualPaused;

    private VirtualApplicationScenario? VirtualApplication => VirtualAppPicker.SelectedItem as VirtualApplicationScenario;

    private void InitializeVirtualApplications()
    {
        _virtualEnabled.UnionWith(VirtualApplicationScenarios.All.Where(app => app.StartsAuthorized).Select(app => app.AppId));
        VirtualDirectionPicker.ItemsSource = DirectionRecognizer.GetDirections(GestureMode.Eight)
            .Select(direction => new VirtualDirectionChoice(direction, GestureLabels.Direction(direction))).ToArray();
        VirtualDirectionPicker.SelectedIndex = 0;
        LoadVirtualProfiles();
    }

    private void VirtualApplicationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingVirtualProfiles)
            return;
        CancelActiveVirtualGesture("切换虚拟应用 · 手势已取消");
        RefreshVirtualApplicationProfiles();
        ShowVirtualApplication();
    }

    private ProtectionStatus VirtualStatus()
    {
        if (VirtualApplication is not { ExecutableName: not null } app)
            return ProtectionStatus.Unmatched;
        if (!_virtualEnabled.Contains(app.AppId))
            return ProtectionStatus.Disabled;
        return _virtualPaused ? ProtectionStatus.Paused : ProtectionStatus.Ready;
    }

    private void ShowVirtualApplication()
    {
        var status = VirtualStatus();
        var profile = ResolveVirtualProfile(status);
        var source = profile is null ? "不可用" : profile.ReadOnly ? "系统默认"
            : profile.AppId is null ? "通用" : "应用专属";
        var active = profile is null ? "当前生效方案 · 无" : $"当前生效方案 · {profile.Name} · {profile.SectorCount} 方向";
        VirtualProfileStatus.Text = $"{active}（{source}）{(_virtualProfileWarning is null ? "" : $"\n{_virtualProfileWarning}")}";
        VirtualAppStatus.Text = status switch
        {
            ProtectionStatus.Ready => "已启用",
            ProtectionStatus.Paused => "已暂停",
            ProtectionStatus.Unmatched => "身份未匹配",
            _ => "未启用"
        };
        VirtualEnable.IsEnabled = status == ProtectionStatus.Disabled;
        VirtualRevoke.IsEnabled = status is ProtectionStatus.Ready or ProtectionStatus.Paused;
        VirtualPause.Content = _virtualPaused ? "恢复手势" : "紧急暂停";
    }

    private void EnableVirtualApplication(object sender, RoutedEventArgs e)
    {
        if (VirtualApplication is not { ExecutableName: not null } app)
            return;

        _virtualEnabled.Add(app.AppId);
        VirtualInputResult.Text = "已启用虚拟侧键。";
        ShowVirtualApplication();
    }

    private void RevokeVirtualApplication(object sender, RoutedEventArgs e)
    {
        if (VirtualApplication is not { } app)
            return;

        _virtualEnabled.Remove(app.AppId);
        VirtualInputResult.Text = "已停用虚拟侧键。";
        CancelActiveVirtualGesture("手势已取消");
        ShowVirtualApplication();
    }

    private void ToggleVirtualPause(object sender, RoutedEventArgs e)
    {
        _virtualPaused = !_virtualPaused;
        CancelActiveVirtualGesture("紧急暂停 · 手势已取消");
        ShowVirtualApplication();
    }

    private void StartVirtualInput(object sender, RoutedEventArgs e)
    {
        if (_activeVirtualProfile is { } captured)
        {
            _activeVirtualProfile = null;
            VirtualInputResult.Text = PreviewVirtualGesture(captured);
            return;
        }

        _activeVirtualProfile = ResolveVirtualProfile(VirtualStatus());
        VirtualInputResult.Text = _activeVirtualProfile is null ? "输入放行 · 无手势"
            : $"虚拟侧键已按下 · {_activeVirtualProfile.Name}";
    }

    private void InvalidVirtualInput(object sender, RoutedEventArgs e) => CancelActiveVirtualGesture("手势已取消 · 无命令");

    private void CancelActiveVirtualGesture(string reason)
    {
        if (_activeVirtualProfile is null)
            return;
        _activeVirtualProfile = null;
        VirtualInputResult.Text = reason;
    }
}
