namespace GestureFlow.Lab.Simulation;

public sealed record VirtualApplicationScenario(string AppId, string DisplayName, string? ExecutableName, bool StartsAuthorized = false)
{
    public string? ExecutablePath => ExecutableName is null ? null : $@"C:\Lab\{ExecutableName}";
}

public static class VirtualApplicationScenarios
{
    public static IReadOnlyList<VirtualApplicationScenario> All { get; } = Array.AsReadOnly(new VirtualApplicationScenario[]
    {
        new("virtual:solidworks", "SolidWorks", "sldworks.exe"),
        new("virtual:photoshop", "Photoshop", "photoshop.exe"),
        new("virtual:word", "Word", "winword.exe", true),
        new("virtual:browser", "浏览器", "browser.exe", true),
        new("virtual:unknown", "未知应用", null)
    });
}
