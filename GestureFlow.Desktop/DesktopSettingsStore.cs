using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop.Input;
using GestureFlow.Profiles;

namespace GestureFlow.Desktop;

// Keep the serialized values used by previous releases.
internal enum WheelSensitivity { High = 16, Standard = 24, Low = 40 }

internal static class WheelSensitivityExtensions
{
    public static double CenterRadius(this WheelSensitivity sensitivity) =>
        GestureWheelMetrics.CenterRadius + (sensitivity switch
        {
            WheelSensitivity.High => -12,
            WheelSensitivity.Low => 12,
            _ => 0
        });

    public static double CrossingDistance(this WheelSensitivity sensitivity) =>
        sensitivity.CenterRadius() + GestureWheelMetrics.CrossingTolerance;
}

internal sealed record DesktopSettings(
    GestureProfile Profile,
    int PauseHotkey = 0,
    WindowCloseAction CloseAction = WindowCloseAction.Ask,
    DesktopTheme Theme = DesktopTheme.System,
    WheelSensitivity Sensitivity = WheelSensitivity.Standard,
    bool WelcomeSeen = false,
    GestureTrigger SideButton = GestureTrigger.MouseX1);

internal sealed class DesktopSettingsStore(string directory, int hotkeyCount)
{
    private sealed record SettingsDocument(
        int Version, JsonElement Profile, int PauseHotkey, WindowCloseAction CloseAction,
        DesktopTheme Theme, WheelSensitivity Sensitivity, bool? WelcomeSeen = null, GestureTrigger SideButton = GestureTrigger.MouseX1);

    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private string SettingsPath => Path.Combine(directory, "desktop-settings.json");
    private string LegacyProfilePath => Path.Combine(directory, "desktop-gesture-profile.json");

    public GestureProfileManager LoadProfiles(out string? migrationWarning)
    {
        migrationWarning = null;
        var manager = GestureProfileManager.Open(directory);
        if (manager.LoadWarning is not null || File.Exists(Path.Combine(directory, GestureProfileManager.FileName)))
            return manager;

        try
        {
            var source = File.Exists(SettingsPath) ? SettingsPath : LegacyProfilePath;
            if (!File.Exists(source))
            {
                manager.Select(null);
                return manager;
            }

            var contents = File.ReadAllText(source);
            var backup = source + ".pre-v1.1.bak";
            if (!File.Exists(backup))
                File.Copy(source, backup);
            else if (!string.Equals(contents, File.ReadAllText(backup), StringComparison.Ordinal))
                File.Copy(source, source + $".{Guid.NewGuid():N}.pre-v1.1.bak");
            var profile = source == SettingsPath ? ReadProfileForMigration(contents) : GestureProfileJson.Deserialize(contents);
            manager.Create("迁移的通用方案", configuration: profile, select: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or JsonException or ProfileValidationException)
        {
            migrationWarning = "旧版笔势迁移失败，已使用系统默认。旧文件仍可恢复。";
        }
        return manager;
    }

    private static GestureProfile ReadProfileForMigration(string contents)
    {
        using var document = JsonDocument.Parse(contents);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != FormatVersion
            || !root.TryGetProperty("profile", out var profile))
            throw new JsonException("找不到支持迁移的旧版手势配置。");
        return GestureProfileJson.Deserialize(profile.GetRawText());
    }

    public DesktopSettings Load(out bool recovered)
    {
        recovered = false;
        try { return Parse(File.ReadAllText(SettingsPath)); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return LoadLegacy(out recovered);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or JsonException or ProfileValidationException)
        {
            recovered = true;
            return new(DefaultGestureProfiles.Create(GestureMode.Four));
        }
    }

    public void Save(DesktopSettings settings) => AtomicWrite(SettingsPath, Serialize(settings, settings.WelcomeSeen));

    public void Export(string path, DesktopSettings settings)
    {
        ValidateExportLocation(path);
        var portable = settings with { Profile = settings.Profile with { Applications = [] } };
        AtomicWrite(path, Serialize(portable, welcomeSeen: null));
    }

    public void ValidateExportLocation(string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        if (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new IOException("请选择 GestureFlow 本地数据目录以外的位置。");

    }

    public DesktopSettings ReadBackup(string path, DesktopSettings current)
    {
        var backup = Parse(File.ReadAllText(path));
        return backup with
        {
            Profile = backup.Profile with { Applications = current.Profile.Applications },
            WelcomeSeen = current.WelcomeSeen
        };
    }

    private DesktopSettings LoadLegacy(out bool recovered)
    {
        var profile = DesktopRuntimeSettings.LoadProfile(LegacyProfilePath,
            out var recoveredProfile);
        var hotkey = DesktopRuntimeSettings.LoadHotkey(Path.Combine(directory, "pause-hotkey.json"),
            hotkeyCount, out var recoveredHotkey);
        var window = DesktopRuntimeSettings.LoadWindowPreferences(Path.Combine(directory, "desktop-window.json"),
            out var recoveredWindow);
        recovered = recoveredProfile || recoveredHotkey || recoveredWindow;
        return new(profile, hotkey, window.CloseAction, WelcomeSeen: window.WelcomeSeen);
    }

    private DesktopSettings Parse(string json)
    {
        var document = JsonSerializer.Deserialize<SettingsDocument>(json, Options)
            ?? throw new JsonException("配置文件应为 JSON 对象。");
        if (document.Version != FormatVersion)
            throw new JsonException("不支持此配置版本。");
        ValidatePreferences(document.PauseHotkey, document.CloseAction, document.Theme, document.Sensitivity, document.SideButton);
        return new(GestureProfileJson.Deserialize(document.Profile.GetRawText()), document.PauseHotkey,
            document.CloseAction, document.Theme, document.Sensitivity, document.WelcomeSeen ?? false, document.SideButton);
    }

    private string Serialize(DesktopSettings settings, bool? welcomeSeen)
    {
        ValidatePreferences(settings.PauseHotkey, settings.CloseAction, settings.Theme, settings.Sensitivity, settings.SideButton);
        using var profile = JsonDocument.Parse(GestureProfileJson.Serialize(settings.Profile));
        return JsonSerializer.Serialize(new SettingsDocument(FormatVersion, profile.RootElement,
            settings.PauseHotkey, settings.CloseAction, settings.Theme, settings.Sensitivity, welcomeSeen, settings.SideButton), Options);
    }

    private void ValidatePreferences(int hotkey, WindowCloseAction closeAction, DesktopTheme theme, WheelSensitivity sensitivity,
        GestureTrigger sideButton)
    {
        if (hotkey < 0 || hotkey >= hotkeyCount || !Enum.IsDefined(closeAction)
            || !Enum.IsDefined(theme) || !Enum.IsDefined(sensitivity)
            || sideButton is not (GestureTrigger.MouseX1 or GestureTrigger.MouseX2))
            throw new JsonException("配置包含无效的热键、关闭行为、主题、灵敏度或鼠标侧键。");
    }

    private static void AtomicWrite(string path, string contents) => GestureProfileManager.AtomicWrite(path, contents);
}
