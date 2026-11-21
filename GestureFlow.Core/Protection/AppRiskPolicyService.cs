namespace GestureFlow.Core.Protection;

public enum RiskLevel { Unknown, Compatible, High }
public enum GestureTrigger { RightButtonDrag, ShiftRightButtonDrag, AltRightButtonDrag, MouseX1, MouseX2 }
public enum RiskMatch { ExactProcessName, ProcessNamePrefix, AumidPrefix }

public sealed record AppRiskRule(string Id, RiskMatch Match, IReadOnlyList<string> Patterns, RiskLevel Level,
    string Reason, string Source, string Version, GestureTrigger RecommendedTrigger);

public sealed record AppRiskAssessment(RiskLevel Level, string Reason, string Source, string Version,
    string PolicyStamp, GestureTrigger RecommendedTrigger);

public sealed class AppRiskPolicyService
{
    private const string CatalogVersion = "2026.11.19";
    private const string CatalogSource = "GestureFlow 内置保守规则";
    private readonly string _revision;
    private readonly IReadOnlyList<AppRiskRule> _rules;

    public AppRiskPolicyService(string revision = CatalogVersion, IReadOnlyList<AppRiskRule>? rules = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        _revision = revision;
        _rules = (rules ?? BuiltIn).Select(rule => rule with { Patterns = Array.AsReadOnly(rule.Patterns.ToArray()) }).ToArray();
    }

    public AppRiskAssessment Assess(string? processName, string? aumid = null, RiskLevel previousRisk = RiskLevel.Unknown)
    {
        var rule = _rules.FirstOrDefault(rule => rule.Level == RiskLevel.High && Matches(rule, processName, aumid))
            ?? _rules.FirstOrDefault(rule => Matches(rule, processName, aumid));
        if (previousRisk == RiskLevel.High && rule?.Level != RiskLevel.High)
            return new(RiskLevel.High, "此应用曾被标记为高风险，右键可能与原生菜单、选择或导航操作冲突。",
                CatalogSource, _revision, $"{_revision}/retained-high", GestureTrigger.AltRightButtonDrag);
        if (rule is null)
            return new(RiskLevel.Unknown, "此应用的兼容性尚未验证，右键可能打开原生菜单。",
                CatalogSource, _revision, $"{_revision}/unknown", GestureTrigger.AltRightButtonDrag);

        // Include rule content so that editing a rule also invalidates its old confirmations.
        var stamp = System.Text.Json.JsonSerializer.Serialize(new { Revision = _revision, Rule = rule });
        return new(rule.Level, rule.Reason, rule.Source, rule.Version, stamp, rule.RecommendedTrigger);
    }

    private static bool Matches(AppRiskRule rule, string? processName, string? aumid) =>
        rule.Patterns.Any(pattern => rule.Match switch
        {
            RiskMatch.ExactProcessName => string.Equals(processName, pattern, StringComparison.OrdinalIgnoreCase),
            RiskMatch.ProcessNamePrefix => processName?.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) == true,
            RiskMatch.AumidPrefix => aumid?.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        });

    private static AppRiskRule High(string id, string reason, string[] names, RiskMatch match = RiskMatch.ExactProcessName) =>
        new(id, match, names, RiskLevel.High, reason, CatalogSource, "1", GestureTrigger.AltRightButtonDrag);

    private static readonly AppRiskRule[] BuiltIn =
    [
        High("solidworks", "CAD 右键可能用于对象或特征菜单、选择以及原生鼠标手势。", ["sldworks.exe"]),
        High("autocad", "绘图时右键可能打开对象菜单、确认命令或重复命令。", ["acad.exe", "acadlt.exe"]),
        High("blender", "三维视图右键可能用于上下文菜单、选择或取消当前操作。", ["blender.exe"]),
        High("photoshop", "画布右键可能打开笔刷、图层或工具选项。", ["photoshop.exe"]),
        High("photoshop-package", "画布右键可能打开笔刷、图层或工具选项。", ["Adobe.Photoshop"], RiskMatch.AumidPrefix),
        High("illustrator", "画板右键可能打开路径、对象或编组菜单。", ["illustrator.exe"]),
        High("premiere", "时间线右键可能打开剪辑、轨道或编辑命令菜单。", ["adobe premiere pro.exe"]),
        High("word", "文档右键可能打开编辑菜单、迷你工具栏或拼写建议。", ["winword.exe"]),
        High("wps", "文档、表格或演示中的右键可能打开编辑菜单和选区工具。", ["wps.exe", "et.exe", "wpp.exe"]),
        High("vscode", "编辑器、终端或文件树中的右键可能调用原生命令。", ["code.exe", "code - insiders.exe", "vscodium.exe"]),
        High("visual-studio", "编辑器、解决方案资源管理器或调试视图的右键可能调用原生命令。", ["devenv.exe"]),
        High("remote-desktop", "右键可能转发到远程会话，或用于远程应用的菜单与交互。",
            ["mstsc.exe", "msrdc.exe", "rdclient.windows.exe", "anydesk.exe", "teamviewer.exe", "rustdesk.exe",
                "parsecd.exe", "parsec.exe", "vmconnect.exe", "wfica32.exe", "vncviewer.exe"]),
        High("games", "游戏右键可能控制视角、瞄准、移动或游戏内交互。",
            ["cs2.exe", "dota2.exe", "league of legends.exe", "minecraft.windows.exe", "overwatch.exe"]),
        High("game-families", "游戏右键可能控制视角、瞄准、移动或游戏内交互。",
            ["fortniteclient-win64-shipping", "valorant-win64-shipping"], RiskMatch.ProcessNamePrefix),
        High("explorer", "桌面和文件夹右键可能打开系统上下文菜单。", ["explorer.exe"])
    ];
}
