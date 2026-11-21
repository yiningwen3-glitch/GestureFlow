using System.IO;
using System.Text.Json;
using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop.Applications;
using GestureFlow.Desktop.Input;
using GestureFlow.Profiles;
using GestureFlow.UI;
using ApplicationIdentity = GestureFlow.Desktop.Applications.ApplicationIdentity;

namespace GestureFlow.Tests;

public sealed class MvpReliabilityTests
{
    [Fact]
    public void DesktopAndLabCanCoexistButEachComponentHasOneInstance()
    {
        var id = Guid.NewGuid().ToString("N");
        using (var desktop = SingleInstanceLease.TryAcquire(id + "Desktop"))
        {
            Assert.NotNull(desktop);
            using var lab = SingleInstanceLease.TryAcquire(id + "Lab");
            Assert.NotNull(lab);
            Assert.Null(SingleInstanceLease.TryAcquire(id + "Desktop"));
            Assert.Null(SingleInstanceLease.TryAcquire(id + "Lab"));
        }

        using var reopened = SingleInstanceLease.TryAcquire(id + "Desktop");
        Assert.NotNull(reopened);
    }

    [Fact]
    public void InvalidProfileAndHotkeyRecoverIndependentlyWithoutOverwriting()
    {
        using var temp = new TemporaryDirectory();
        var profilePath = System.IO.Path.Combine(temp.Path, "profile.json");
        var hotkeyPath = System.IO.Path.Combine(temp.Path, "hotkey.json");
        File.WriteAllText(profilePath, GestureProfileJson.Serialize(DefaultGestureProfiles.Create(GestureMode.Eight)));
        File.WriteAllText(hotkeyPath, "99");

        Assert.Equal(0, DesktopRuntimeSettings.LoadHotkey(hotkeyPath, 3, out var hotkeyRecovered));
        Assert.True(hotkeyRecovered);
        Assert.Equal(GestureMode.Eight, DesktopRuntimeSettings.LoadProfile(profilePath, out var profileRecovered).Mode);
        Assert.False(profileRecovered);
        Assert.Equal("99", File.ReadAllText(hotkeyPath));

        File.WriteAllText(hotkeyPath, "2");
        File.WriteAllText(profilePath, "{broken");
        Assert.Equal(2, DesktopRuntimeSettings.LoadHotkey(hotkeyPath, 3, out hotkeyRecovered));
        Assert.False(hotkeyRecovered);
        Assert.Equal(GestureMode.Four, DesktopRuntimeSettings.LoadProfile(profilePath, out profileRecovered).Mode);
        Assert.True(profileRecovered);
        Assert.Equal("{broken", File.ReadAllText(profilePath));
    }

    [Fact]
    public void ReplacingAnExecutableAtTheSamePathMakesItsProtectedGrantStale()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "sldworks.exe");
        var replacement = System.IO.Path.Combine(temp.Path, "replacement.exe");
        File.WriteAllBytes(path, [1, 2, 3]);
        var initial = Assert.IsType<string>(ExecutableInstallationStamp.Read(path));
        Assert.Equal(initial, ExecutableInstallationStamp.Read(path));
        var risk = new AppRiskPolicyService().Assess("sldworks.exe");
        var target = new ProtectionTarget(ApplicationIdentity.ForPath(path), path, risk, initial);
        var store = new ApplicationStateStore(temp.Path);
        store.SaveConfirmedPolicy(target, Confirm(target));
        var policies = store.LoadPolicies();
        var authorizations = store.LoadAuthorizations();
        var savedPolicy = policies[target.AppId];
        Assert.NotNull(ApplicationGesturePolicy.VerifiedAuthorization(target, savedPolicy, authorizations));
        Assert.Null(ApplicationGesturePolicy.VerifiedAuthorization(target with { InstallationStamp = null }, savedPolicy, authorizations));

        var lastWrite = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(replacement, [1, 2, 3]);
        File.SetLastWriteTimeUtc(replacement, lastWrite);
        File.Move(replacement, path, overwrite: true);
        var latest = Assert.IsType<string>(ExecutableInstallationStamp.Read(path));
        Assert.NotEqual(initial, latest);
        Assert.Null(ApplicationGesturePolicy.VerifiedAuthorization(target with { InstallationStamp = latest }, savedPolicy, authorizations));
    }

    [Fact]
    public void SideButtonEnableStoresTheInstallationWithoutAConfirmationWizard()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "sldworks.exe");
        var target = new ProtectionTarget(ApplicationIdentity.ForPath(path), path,
            new AppRiskPolicyService().Assess("sldworks.exe"), "first-installation");
        var store = new ApplicationStateStore(temp.Path);
        Assert.Throws<JsonException>(() => store.EnableSideButton(target, GestureTrigger.RightButtonDrag));
        Assert.True(store.EnableSideButton(target, GestureTrigger.MouseX2)[target.AppId].Allowed);
        var saved = store.LoadAuthorizations()[target.AppId];
        Assert.Equal(GestureTrigger.MouseX2, saved.Trigger);
        Assert.Equal(target.InstallationStamp, saved.InstallationStamp);
    }

    [Fact]
    public void ResetRecoversCorruptProtectionAndRevokesPureRightWithoutChangingBindings()
    {
        using var temp = new TemporaryDirectory();
        var executable = System.IO.Path.Combine(temp.Path, "sldworks.exe");
        var profile = System.IO.Path.Combine(temp.Path, "desktop-gesture-profile.json");
        var policyFile = System.IO.Path.Combine(temp.Path, "application-policies.json");
        var authorizationFile = System.IO.Path.Combine(temp.Path, "application-authorizations.dat");
        var store = new ApplicationStateStore(temp.Path);
        var target = new ProtectionTarget(ApplicationIdentity.ForPath(executable), executable,
            new AppRiskPolicyService().Assess("sldworks.exe"), "old-installation");
        store.SaveConfirmedPolicy(target, Confirm(target, GestureTrigger.RightButtonDrag));
        store.AddManualApplication(new(executable));
        var backupPolicy = File.ReadAllText(policyFile);
        var profileJson = GestureProfileJson.Serialize(DefaultGestureProfiles.Create(GestureMode.Eight));
        File.WriteAllText(profile, profileJson);
        File.WriteAllText(policyFile, "broken");
        File.WriteAllBytes(authorizationFile, [1, 2, 3]);
        Assert.Throws<JsonException>(() => store.LoadPolicies());
        Assert.Throws<System.ComponentModel.Win32Exception>(() => store.LoadAuthorizations());

        store.ResetProtection();
        Assert.Empty(store.LoadPolicies());
        Assert.Empty(store.LoadAuthorizations());
        Assert.Equal(profileJson, File.ReadAllText(profile));
        Assert.Single(store.LoadManualApplications());
        File.WriteAllText(policyFile, backupPolicy);
        Assert.Null(ApplicationGesturePolicy.VerifiedAuthorization(target, store.LoadPolicies()[target.AppId], store.LoadAuthorizations()));
    }

    [Theory]
    [InlineData(0x218, 4, 0xC000, 1)]
    [InlineData(0x218, 7, 0xC000, 1)]
    [InlineData(0x218, 18, 0xC000, 1)]
    [InlineData(0xC000, 0, 0xC000, 2)]
    [InlineData(0x7E, 0, 0xC000, 3)]
    [InlineData(0x218, 5, 0xC000, 0)]
    [InlineData(0, 0, 0, 0)]
    public void SystemMessagesHaveExplicitRecoveryPaths(int message, int parameter, uint shell, int expected) =>
        Assert.Equal((RuntimeSystemEvent)expected, DesktopRuntimeSettings.SystemEvent(message, parameter, shell));

    [Theory]
    [InlineData(96u, 228, 1.0)]
    [InlineData(144u, 342, 1.5)]
    [InlineData(192u, 456, 2.0)]
    public void WheelStaysInNegativeCoordinateMonitorWorkArea(uint dpi, int size, double scale)
    {
        var work = new WindowsInputNative.Rect { Left = -1920, Top = -900, Right = 0, Bottom = 140 };
        var corner = WheelPlacement.Around(new(-1919, -898), dpi, work);
        Assert.Equal((-1920, -900, size, scale), (corner.X, corner.Y, corner.Size, corner.Scale));
        var otherCorner = WheelPlacement.Around(new(-1, 139), dpi, work);
        Assert.Equal((-size, 140 - size, size, scale), (otherCorner.X, otherCorner.Y, otherCorner.Size, otherCorner.Scale));
    }

    [Fact]
    public void ExistingMouseButtonAndRapidForegroundChangesDoNotLeakCaptures()
    {
        var profile = DefaultGestureProfiles.Create(GestureMode.Four);
        var lease = new InputPermission(24, 25, 51, 0, 0, long.MaxValue, "test", GestureTrigger.MouseX1, profile, default);
        int presses = 0, cancels = 0, releases = 0;
        var router = new MouseGestureRouter(frame =>
        {
            if (frame.Input.Kind == GestureInputKind.Press) presses++;
            if (frame.Input.Kind == GestureInputKind.Cancel) cancels++;
            if (frame.Input.Kind == GestureInputKind.Release) releases++;
        });

        for (long iteration = 0; iteration < 20_000; iteration++)
        {
            var allow = lease with { FocusEpoch = iteration * 2 };
            var down = new MousePacket(MouseSignal.Down, MouseButton.X1, default, iteration * 1000, ShortcutModifiers.None);
            Assert.False(router.Route(down with { OtherButtonHeld = true }, allow, true));
            Assert.True(router.Route(down, allow, true));
            Assert.True(router.Route(down with { Signal = MouseSignal.Up, Tick = down.Tick + 501 }, allow with { FocusEpoch = allow.FocusEpoch + 1 }, true));
            Assert.False(router.OwnsButton);
        }

        Assert.Equal((20_000, 20_000, 0), (presses, cancels, releases));
    }

    private static CompletedAuthorization Confirm(ProtectionTarget target, GestureTrigger trigger = GestureTrigger.AltRightButtonDrag)
    {
        var flow = new AuthorizationFlow(target);
        Assert.True(flow.AcknowledgeRisk());
        Assert.True(flow.Review(trigger));
        return Assert.IsType<CompletedAuthorization>(flow.Confirm(target));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("GestureFlow-Mvp-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
