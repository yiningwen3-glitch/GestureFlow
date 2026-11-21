using GestureFlow.Core.Protection;
using GestureFlow.Profiles;

namespace GestureFlow.UI;

public enum DockGestureMode { Stopped, Running, Paused, TestOnly }

public sealed record DockProfileOption(string AppId, Guid? ProfileId, string Label, bool Selected);

public sealed record FloatingDockState(
    string Application, string Profile, ProtectionStatus Protection, DockGestureMode Mode,
    IReadOnlyList<DockProfileOption> Options, bool HighRisk, bool InputRestricted)
{
    public static FloatingDockState ForApplication(string application, string? appId, GestureProfileManager profiles,
        ProtectionStatus protection, DockGestureMode mode, bool highRisk = false, bool inputRestricted = false)
    {
        var selected = appId is null ? null : profiles.Selected(appId);
        DockProfileOption[] options = appId is null ? [] :
            [new(appId, null, "使用通用配置", selected is null), .. profiles.Profiles
                .Where(profile => string.Equals(profile.AppId, appId, StringComparison.OrdinalIgnoreCase))
                .Select(profile => new DockProfileOption(appId, profile.ProfileId, profile.Name,
                    profile.ProfileId == selected?.ProfileId))];
        var profile = profiles.ConfiguredFor(appId);
        return new(application, profile.Name, protection, mode, Array.AsReadOnly(options), highRisk, inputRestricted);
    }

    public string ModeLabel => Mode switch
    {
        DockGestureMode.Running => "运行中",
        DockGestureMode.Paused => "已暂停",
        DockGestureMode.TestOnly => "仅测试",
        _ => "已关闭"
    };

    public string ProtectionLabel => InputRestricted ? "受保护 · 系统权限限制" : Protection switch
    {
        ProtectionStatus.Ready or ProtectionStatus.Paused => HighRisk ? "高风险 · 已授权" : "已授权",
        ProtectionStatus.Disabled => "受保护 · 已禁用",
        ProtectionStatus.AuthorizationRequired => "受保护 · 未授权",
        _ => "受保护 · 身份未匹配"
    };

    public bool CanTogglePause => Mode is DockGestureMode.Running or DockGestureMode.Paused;
    public string PauseLabel => Mode == DockGestureMode.Paused ? "恢复笔势" : "暂停笔势";
    public string ProfileLabel => !InputRestricted && Protection is ProtectionStatus.Ready or ProtectionStatus.Paused
        ? Profile : $"待用 · {Profile}";
}
