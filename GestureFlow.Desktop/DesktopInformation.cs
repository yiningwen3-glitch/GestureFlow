using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GestureFlow.Desktop;

internal sealed record OfflineArticle(string Title, string? ResourceName = null, string? FilePath = null, bool IsMarkdown = true);

internal sealed class DesktopInformation
{
    private const string ResourcePrefix = "GestureFlow.Offline.";
    private static readonly Assembly DesktopAssembly = typeof(DesktopInformation).Assembly;

    public string Version { get; } = DesktopAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+', 2)[0] ?? DesktopAssembly.GetName().Version?.ToString() ?? "未知";
    public string Developer { get; } = ReadMetadata("GestureFlowDeveloper");
    public string DeveloperContact { get; } = ReadMetadata("GestureFlowDeveloperContact");

    public IReadOnlyList<OfflineArticle> HelpTopics { get; } =
    [
        new("使用指南", "USER_GUIDE.md"),
        new("应用保护与使用须知", "SAFETY.md"),
        new("常见问题与故障排查", "FAQ.md"),
        new("兼容性说明", "COMPATIBILITY.md")
    ];

    public IReadOnlyList<OfflineArticle> AboutTopics { get; } = GetAboutTopics();

    public static string Read(OfflineArticle article)
    {
        if (article.FilePath is { } path)
            return File.ReadAllText(path);

        if (article.ResourceName is null)
            return "此构建未附带 GestureFlow 项目许可证。项目授权条款待开发者提供。";

        using var stream = DesktopAssembly.GetManifestResourceStream(ResourcePrefix + article.ResourceName)
            ?? throw new FileNotFoundException("找不到该离线文档。", article.ResourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<OfflineArticle> GetAboutTopics()
    {
        var projectLicense = DesktopAssembly.GetManifestResourceInfo(ResourcePrefix + "LICENSE") is not null
            ? "LICENSE" : null;
        var topics = new List<OfflineArticle>
        {
            new("更新日志", "CHANGELOG.md"),
            new("隐私声明", "PRIVACY.md"),
            new("第三方组件", "THIRD_PARTY.md"),
            new("GestureFlow 项目许可证", projectLicense, IsMarkdown: false)
        };

        var portableLicenses = Path.Combine(AppContext.BaseDirectory, "..", "licenses");
        AddNotice(topics, "便携版 .NET Runtime · 许可证", portableLicenses, "Microsoft.NETCore.App-LICENSE.TXT");
        AddNotice(topics, "便携版 .NET Runtime · 第三方声明", portableLicenses, "Microsoft.NETCore.App-THIRD-PARTY-NOTICES.TXT");
        AddNotice(topics, "便携版 .NET Desktop Runtime · 许可证", portableLicenses, "Microsoft.WindowsDesktop.App-LICENSE");
        AddNotice(topics, "便携版 .NET Desktop Runtime · 第三方声明", portableLicenses, "Microsoft.WindowsDesktop.App-THIRD-PARTY-NOTICES.TXT");

        var sharedDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent;
        if (sharedDirectory is { Name: "shared", Parent: { } dotnetDirectory })
        {
            AddNotice(topics, "本机 .NET 安装 · 许可证", dotnetDirectory.FullName, "LICENSE.txt");
            AddNotice(topics, "本机 .NET 安装 · 第三方声明", dotnetDirectory.FullName, "ThirdPartyNotices.txt");
        }

        return topics.AsReadOnly();
    }

    private static void AddNotice(List<OfflineArticle> topics, string title, string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path))
            topics.Add(new(title, FilePath: path, IsMarkdown: false));
    }

    private static string ReadMetadata(string key)
    {
        var value = DesktopAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? "未设置" : value;
    }
}
