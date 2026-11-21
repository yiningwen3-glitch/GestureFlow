using GestureFlow.Core.Protection;

namespace GestureFlow.UI;

public static class ProtectionLabels
{
    public const string ProtectedAppNotice = "启用笔势后，所选鼠标侧键可能占用应用的前进或后退功能。";
    public const string Measures = "紧急暂停 · 异常输入取消 · 切换应用时取消";

    public static string Trigger(GestureTrigger trigger) => trigger switch
    {
        GestureTrigger.AltRightButtonDrag => "Alt + 右键拖动",
        GestureTrigger.ShiftRightButtonDrag => "Shift + 右键拖动",
        GestureTrigger.MouseX1 => "鼠标侧键 1 拖动",
        GestureTrigger.MouseX2 => "鼠标侧键 2 拖动",
        _ => "右键长按 0.5 秒后拖动"
    };

    public static string RemainingRisk(GestureTrigger trigger) => trigger switch
    {
        GestureTrigger.RightButtonDrag => "纯右键与应用原生右键使用同一个按钮，可能影响原生菜单、选择或拖动。",
        GestureTrigger.MouseX1 or GestureTrigger.MouseX2 => "侧键可能与返回、前进或应用自定义功能冲突。",
        _ => "组合键可保留普通右键操作，仍可能与应用已有的同组合键冲突。"
    };

    public static string Status(ProtectionStatus status) => status switch
    {
        ProtectionStatus.Ready => "已允许",
        ProtectionStatus.Paused => "已暂停",
        ProtectionStatus.Disabled => "已禁用 · 已解除授权",
        ProtectionStatus.Unmatched => "已禁用 · 身份未匹配",
        _ => "已禁用 · 待授权"
    };
}
