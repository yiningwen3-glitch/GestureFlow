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
    private sealed record VirtualProfileChoice(Guid? Id, string Name);
    private sealed record VirtualDirectionChoice(GestureDirection Direction, string Name);

    private readonly string _profileDirectory;
    private GestureProfileManager _virtualProfiles = new();
    private string? _virtualProfileWarning;
    private ManagedGestureProfile? _activeVirtualProfile;
    private bool _syncingVirtualProfiles;

    private void ReloadVirtualProfilesClick(object sender, RoutedEventArgs e) => LoadVirtualProfiles();

    private void LoadVirtualProfiles()
    {
        var appId = VirtualApplication?.AppId;
        var saved = GestureProfileManager.Open(_profileDirectory);
        _virtualProfileWarning = saved.LoadWarning;
        _virtualProfiles = saved.CreatePreview();
        var existing = VirtualApplicationScenarios.All.Select(app => app.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = _virtualProfiles.Profiles.Where(profile => profile.AppId is not null && !existing.Contains(profile.AppId))
            .GroupBy(profile => profile.AppId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VirtualApplicationScenario(group.Key, $"{group.First().Name} · 已保存应用",
                "gestureflow-preview.exe"));
        var applications = VirtualApplicationScenarios.All.Concat(extra).ToArray();
        CancelActiveVirtualGesture("重新加载方案 · 手势已取消");
        _syncingVirtualProfiles = true;
        try
        {
            VirtualAppPicker.ItemsSource = applications;
            VirtualAppPicker.SelectedItem = applications.FirstOrDefault(app => app.AppId == appId) ?? applications[0];
            var globals = _virtualProfiles.Profiles.Where(profile => profile.Scope == GestureProfileScope.Global)
                .Select(profile => new VirtualProfileChoice(profile.ProfileId, profile.Name)).ToArray();
            VirtualGlobalProfilePicker.ItemsSource = globals;
            VirtualGlobalProfilePicker.SelectedItem = globals.FirstOrDefault(choice =>
                choice.Id == _virtualProfiles.Selected()?.ProfileId);
        }
        finally { _syncingVirtualProfiles = false; }
        RefreshVirtualApplicationProfiles();
        ShowVirtualApplication();
    }

    private void RefreshVirtualApplicationProfiles()
    {
        var app = VirtualApplication;
        _syncingVirtualProfiles = true;
        try
        {
            VirtualProfileChoice[] choices = [new(null, "跟随通用方案"), .. _virtualProfiles.Profiles
                .Where(profile => profile.AppId is not null && string.Equals(profile.AppId, app?.AppId, StringComparison.OrdinalIgnoreCase))
                .Select(profile => new VirtualProfileChoice(profile.ProfileId, profile.Name))];
            VirtualAppProfilePicker.ItemsSource = choices;
            VirtualAppProfilePicker.SelectedItem = choices.First(choice => choice.Id == _virtualProfiles.Selected(app?.AppId)?.ProfileId);
            VirtualAppProfilePicker.IsEnabled = choices.Length > 1;
        }
        finally { _syncingVirtualProfiles = false; }
    }

    private void VirtualGlobalProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingVirtualProfiles && VirtualGlobalProfilePicker.SelectedItem is VirtualProfileChoice choice)
        {
            CancelActiveVirtualGesture("切换方案 · 手势已取消");
            _virtualProfiles.Select(choice.Id);
            ShowVirtualApplication();
        }
    }

    private void VirtualAppProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingVirtualProfiles && VirtualApplication is { } app
            && VirtualAppProfilePicker.SelectedItem is VirtualProfileChoice choice)
        {
            _virtualProfiles.Select(choice.Id, app.AppId);
            ShowVirtualApplication();
        }
    }

    private ManagedGestureProfile? ResolveVirtualProfile(ProtectionStatus protection) =>
        _virtualProfiles.Resolve(VirtualApplication?.AppId, protection);

    private string PreviewVirtualGesture(ManagedGestureProfile profile)
    {
        var direction = (VirtualDirectionPicker.SelectedItem as VirtualDirectionChoice)?.Direction ?? GestureDirection.North;
        return SimulateDirection(profile, direction) is { } preview
            ? $"模拟执行 · {profile.Name} · {preview.DisplayName} · {GestureLabels.Shortcut(preview.Shortcut)}"
            : $"模拟手势已完成 · {profile.Name} · 无命令";
    }

    private static GesturePreview? SimulateDirection(ManagedGestureProfile profile, GestureDirection direction)
    {
        var session = new GestureLabSession(profile.Configuration);
        return GestureScenarios.Direction(direction).Select(session.Process)
            .FirstOrDefault(trace => trace.Preview is not null)?.Preview;
    }
}
