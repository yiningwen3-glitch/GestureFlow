using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop;
using GestureFlow.Profiles;

namespace GestureFlow.Tests;

public sealed class ProfileManagerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("GestureFlow-Profiles-");
    private string Catalog => Path.Combine(_root.FullName, GestureProfileManager.FileName);

    [Fact]
    public void SavesSeveralProfilesAndEachAppSelectionIndependently()
    {
        var manager = GestureProfileManager.Open(_root.FullName);
        var global = manager.Create("原通用", select: true);
        var alternate = manager.Copy(global.ProfileId, "通用副本");
        var first = manager.Create("应用一", "app-a", select: true);
        var other = manager.Create("应用一备用", "app-a", select: true);
        var second = manager.Create("应用二", "app-b", select: true);
        manager.Edit(other.ProfileId, DefaultGestureProfiles.Create(GestureMode.Eight));
        manager.Rename(alternate.ProfileId, "新通用");
        manager.Select(alternate.ProfileId);

        manager = GestureProfileManager.Open(_root.FullName);
        Assert.Equal(6, manager.Profiles.Count);
        Assert.Equal("新通用", manager.Selected()!.Name);
        Assert.Equal(other.ProfileId, manager.Resolve("APP-A", ProtectionStatus.Ready)!.ProfileId);
        Assert.Equal(8, manager.Selected("app-a")!.SectorCount);
        Assert.Equal(second.ProfileId, manager.Resolve("app-b", ProtectionStatus.Ready)!.ProfileId);
        Assert.Equal(alternate.ProfileId, manager.Resolve("app-c", ProtectionStatus.Ready)!.ProfileId);
        Assert.Throws<InvalidOperationException>(() => manager.Select(first.ProfileId, "app-b"));
        Assert.Throws<InvalidOperationException>(() => manager.Select(Guid.NewGuid()));
        manager.Delete(other.ProfileId);
        Assert.Equal(alternate.ProfileId, manager.Resolve("app-a", ProtectionStatus.Ready)!.ProfileId);
        manager.Delete(alternate.ProfileId);
        Assert.Same(GestureProfileManager.SystemDefault, manager.Resolve("app-a", ProtectionStatus.Ready));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptGlobalAndDanglingSelectionsFallBackAndOriginalIsRecoverable(bool missingSelection)
    {
        var manager = GestureProfileManager.Open(_root.FullName);
        manager.Create("损坏的通用", select: true);
        var valid = manager.Create("有效", "app-a", select: true);
        var document = JsonNode.Parse(File.ReadAllText(Catalog))!.AsObject();
        document["profiles"]![0]!["sectorCount"] = 6;
        if (missingSelection)
            document.Remove("global");
        else
            document["global"] = "malformed-selection";
        document["applications"]!["app-b"] = Guid.NewGuid().ToString();
        var damaged = document.ToJsonString();
        File.WriteAllText(Catalog, damaged);

        manager = GestureProfileManager.Open(_root.FullName);
        Assert.NotNull(manager.LoadWarning);
        Assert.Equal(valid.ProfileId, manager.Resolve("app-a", ProtectionStatus.Ready)!.ProfileId);
        Assert.Same(GestureProfileManager.SystemDefault, manager.Resolve("app-b", ProtectionStatus.Ready));
        manager.Select(null);
        Assert.Equal(damaged, File.ReadAllText(Assert.Single(Directory.GetFiles(_root.FullName, "*.recovery"))));
    }

    [Fact]
    public void ImportsAreEditableAndCannotReplaceExistingIdsOrSystemDefault()
    {
        var manager = new GestureProfileManager();
        var system = GestureProfileManager.SystemDefault;
        Assert.Throws<InvalidOperationException>(() => manager.Delete(system.ProfileId));
        Assert.Throws<InvalidOperationException>(() => manager.Rename(system.ProfileId, "改名"));
        Assert.Throws<InvalidOperationException>(() => manager.Edit(system.ProfileId, system.Configuration));
        var copy = manager.Copy(system.ProfileId, "可编辑");
        var import = manager.Import(manager.Export(copy.ProfileId), "app-a");
        var defaultImport = manager.Import(manager.Export(system.ProfileId));
        Assert.NotEqual(copy.ProfileId, import.ProfileId);
        Assert.NotEqual(system.ProfileId, defaultImport.ProfileId);
        Assert.False(defaultImport.ReadOnly);
        Assert.Equal("app-a", import.AppId);
        Assert.Null(manager.Selected("app-a"));
        Assert.Throws<JsonException>(() => manager.Import("{}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MigratesBothLegacyFormatsAndPreservesProtectionFiles(bool combined)
    {
        var store = new DesktopSettingsStore(_root.FullName, 3);
        var profile = DefaultGestureProfiles.Create(GestureMode.Eight);
        var custom = profile with
        {
            Bindings = profile.Bindings.Select(binding => binding with
            {
                DisplayName = $"原操作{binding.Direction}",
                Shortcut = new(ShortcutKey.F12, ShortcutModifiers.Shift)
            }).ToArray(),
            Applications = [new("sldworks.exe", true, true, true)]
        };
        var source = Path.Combine(_root.FullName, combined ? "desktop-settings.json" : "desktop-gesture-profile.json");
        if (combined)
        {
            store.Save(new(custom));
            var document = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
            document["pauseHotkey"] = -1;
            File.WriteAllText(source, document.ToJsonString());
        }
        else
            File.WriteAllText(source, GestureProfileJson.Serialize(custom));
        var policies = Path.Combine(_root.FullName, "application-policies.json");
        File.WriteAllText(policies, "original-protection-data");
        var original = File.ReadAllText(source);

        var manager = store.LoadProfiles(out var warning);
        Assert.Null(warning);
        var migrated = manager.Selected()!;
        Assert.False(migrated.ReadOnly);
        Assert.Equal(8, migrated.SectorCount);
        Assert.Equal(custom.Bindings, migrated.Gestures);
        Assert.Empty(migrated.Configuration.Applications);
        Assert.Equal(original, File.ReadAllText(source));
        Assert.Equal(original, File.ReadAllText(source + ".pre-v1.1.bak"));
        Assert.Equal("original-protection-data", File.ReadAllText(policies));
        Assert.Equal(migrated.ProfileId, store.LoadProfiles(out _).Selected()!.ProfileId);
        Assert.Null(manager.Resolve("sldworks", ProtectionStatus.AuthorizationRequired));
    }

    [Fact]
    public void FailedMigrationPreservesOriginalAndCanBeRetriedAfterRepair()
    {
        var store = new DesktopSettingsStore(_root.FullName, 3);
        var source = Path.Combine(_root.FullName, "desktop-gesture-profile.json");
        File.WriteAllText(source, "invalid");
        Assert.Same(GestureProfileManager.SystemDefault, store.LoadProfiles(out var warning).Selected());
        Assert.NotNull(warning);
        Assert.False(File.Exists(Catalog));
        Assert.Equal("invalid", File.ReadAllText(source + ".pre-v1.1.bak"));
        File.WriteAllText(source, GestureProfileJson.Serialize(DefaultGestureProfiles.Create(GestureMode.Eight)));
        Assert.Equal(8, store.LoadProfiles(out warning).Selected()!.SectorCount);
        Assert.Null(warning);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
