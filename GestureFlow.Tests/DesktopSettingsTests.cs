using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop;
using GestureFlow.Profiles;

namespace GestureFlow.Tests;

public sealed class DesktopSettingsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("GestureFlow-Settings-");

    [Fact]
    public void ReadsLegacyFilesAndSavesWithoutChangingThem()
    {
        var store = new DesktopSettingsStore(_root.FullName, 3);
        var profilePath = Path.Combine(_root.FullName, "desktop-gesture-profile.json");
        var legacy = GestureProfileJson.Serialize(DefaultGestureProfiles.Create(GestureMode.Eight));
        File.WriteAllText(profilePath, legacy);
        File.WriteAllText(Path.Combine(_root.FullName, "pause-hotkey.json"), "2");
        File.WriteAllText(Path.Combine(_root.FullName, "desktop-window.json"), """{"WelcomeSeen":true,"CloseAction":1}""");

        var settings = store.Load(out var recovered);
        Assert.False(recovered);
        Assert.True(settings.WelcomeSeen);
        Assert.Equal(2, settings.PauseHotkey);
        Assert.Equal(WindowCloseAction.Hide, settings.CloseAction);
        Assert.Equal(GestureMode.Eight, settings.Profile.Mode);
        Assert.Equal(WheelSensitivity.Standard, settings.Sensitivity);

        Assert.Equal(GestureTrigger.MouseX1, settings.SideButton);
        store.Save(settings with { Theme = DesktopTheme.Dark, Sensitivity = WheelSensitivity.High, SideButton = GestureTrigger.MouseX2 });
        var reloaded = store.Load(out recovered);
        Assert.False(recovered);
        Assert.Equal(DesktopTheme.Dark, reloaded.Theme);
        Assert.Equal(WheelSensitivity.High, reloaded.Sensitivity);
        Assert.True(reloaded.WelcomeSeen);
        Assert.Equal(GestureTrigger.MouseX2, reloaded.SideButton);
        Assert.Equal(legacy, File.ReadAllText(profilePath));
    }

    [Fact]
    public void PortableBackupRoundTripsPreferencesWithoutCopyingApplicationRulesOrWelcomeState()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root.FullName, "local"));
        var path = Path.Combine(_root.FullName, "backup.json");
        var store = new DesktopSettingsStore(directory.FullName, 3);
        var profile = DefaultGestureProfiles.Create(GestureMode.Eight) with
        {
            Applications = [new("source.exe", true, false, false)]
        };
        var source = new DesktopSettings(profile, 1, WindowCloseAction.Hide, DesktopTheme.Dark, WheelSensitivity.Low, true);
        store.Export(path, source);

        using var backup = JsonDocument.Parse(File.ReadAllText(path));
        var root = backup.RootElement;
        Assert.False(root.TryGetProperty("welcomeSeen", out _));
        Assert.Equal(0, root.GetProperty("profile").GetProperty("applications").GetArrayLength());
        var current = new DesktopSettings(DefaultGestureProfiles.Create(GestureMode.Four) with
        {
            Applications = [new("destination.exe", false, false, false)]
        });
        var restored = store.ReadBackup(path, current);
        Assert.False(restored.WelcomeSeen);
        Assert.Equal("destination.exe", Assert.Single(restored.Profile.Applications).ExecutableName);
        Assert.Equal(profile.Bindings, restored.Profile.Bindings);
        Assert.Equal((1, WindowCloseAction.Hide, DesktopTheme.Dark, WheelSensitivity.Low),
            (restored.PauseHotkey, restored.CloseAction, restored.Theme, restored.Sensitivity));
    }

    [Fact]
    public void InvalidBackupsAndManagedExportPathsLeaveCurrentSettingsIntact()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root.FullName, "local"));
        var path = Path.Combine(_root.FullName, "backup.json");
        var store = new DesktopSettingsStore(directory.FullName, 3);
        var settings = new DesktopSettings(DefaultGestureProfiles.Create(GestureMode.Four), PauseHotkey: 2);
        store.Save(settings);
        store.Export(path, settings);

        Assert.Throws<IOException>(() => store.Export(Path.Combine(directory.FullName, "application-policies.json"), settings));
        var backup = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        backup["pauseHotkey"] = -1;
        File.WriteAllText(path, backup.ToJsonString());
        Assert.Throws<JsonException>(() => store.ReadBackup(path, settings));
        Assert.Equal(2, store.Load(out _).PauseHotkey);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
