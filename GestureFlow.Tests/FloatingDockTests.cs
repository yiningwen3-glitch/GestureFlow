using System.IO;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop.Applications;
using GestureFlow.Profiles;
using GestureFlow.UI;

namespace GestureFlow.Tests;

public sealed class FloatingDockTests
{
    [Fact]
    public void BusinessContextSurvivesOwnWindowsAndRejectsClosedOrReplacedTargets()
    {
        var appA = new RunningApplication(100, 1, 1, "Window", ForegroundWindowKind.DesktopProgram,
            "first.exe", null, null, null);
        var appB = appA with { WindowHandle = 200, WindowOwnerProcessId = 2, EffectiveProcessId = 2, ExecutableName = "second.exe" };
        var own = appA with { WindowHandle = 900, WindowOwnerProcessId = 9, EffectiveProcessId = 9 };
        var context = new BusinessForegroundContext(own.WindowOwnerProcessId);
        var open = new HashSet<RunningApplication> { appA, appB, own };
        ForegroundWindowIdentity Identity(RunningApplication app) => new(app.WindowHandle, app.WindowOwnerProcessId);

        Assert.True(context.Observe(appA, Identity(appA), open.Contains));
        Assert.False(context.Observe(own, Identity(own), open.Contains));
        Assert.Same(appA, context.ForProfileSelection(Identity(own), open.Contains));
        Assert.Null(context.ForProfileSelection(Identity(appB), open.Contains));
        Assert.True(context.Observe(appA, Identity(appB), open.Contains));
        Assert.Null(context.Target);
        Assert.True(context.Observe(appB, Identity(appB), open.Contains));
        Assert.Same(appB, context.ForProfileSelection(Identity(appB), open.Contains));
        Assert.Null(context.ForProfileSelection(new(appB.WindowHandle, 77), open.Contains));
        open.Remove(appB);
        Assert.Null(context.ForProfileSelection(Identity(own), open.Contains));
        Assert.True(context.Observe(own, Identity(own), open.Contains));
        Assert.Null(context.Target);
    }

    [Fact]
    public void SharedDockNativeImportsOnlyHandleWindowPlacementAndPointerState()
    {
        var imports = typeof(FloatingDockWindow).GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods()).Where(method =>
                (method.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0)
            .Select(method => method.Name).Order().ToArray();
        Assert.Equal(new[] { "GetAsyncKeyState", "GetCursorPos", "GetMonitorInfo", "GetWindowLongPtr",
            "GetWindowRect", "MonitorFromPoint", "SetWindowLongPtr", "SetWindowPos" }, imports);
    }

    [Theory]
    [InlineData(332, 72, -332, -12)]
    [InlineData(498, 108, -498, -48)]
    [InlineData(664, 144, -664, -84)]
    public void PlacementFitsNegativeCoordinateMonitorAtDifferentPhysicalSizes(int width, int height, int x, int y)
    {
        var area = new DockWorkArea(-1920, -1020, 0, 60);
        Assert.Equal(new DockPosition(x, y), area.Fit(new(-2, 59), width, height));
        Assert.Equal(new DockPosition(-1920, -1020), area.Fit(new(-5000, -5000), width, height));
        Assert.Equal(new DockPosition(x - 16, y - 24), area.Fit(null, width, height));
    }

    [Fact]
    public void PreferencesPersistVisibilityPlacementAndPresentationAndRecoverFromDamage()
    {
        var directory = Directory.CreateTempSubdirectory("GestureFlow-Dock-");
        try
        {
            var path = Path.Combine(directory.FullName, "dock.json");
            var preferences = new FloatingDockPreferences(true, false, false, false, 0.66, new(-1180, 220));
            preferences.Save(path);
            Assert.Equal(preferences, FloatingDockPreferences.Load(path, out var recovered));
            Assert.False(recovered);
            File.WriteAllText(path, """{"Opacity":0.03}""");
            Assert.Equal(new FloatingDockPreferences(), FloatingDockPreferences.Load(path, out recovered));
            Assert.True(recovered);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void ProtectedDockShowsConfiguredOptionsWithoutProducingAnEffectiveGesture()
    {
        var profiles = new GestureProfileManager();
        var global = profiles.Create("通用自定义", select: true);
        var protectedProfile = profiles.Create("保护应用", "app-a", select: true);
        var other = profiles.Create("其他应用", "app-b", select: true);
        var state = FloatingDockState.ForApplication("App A", "app-a", profiles,
            ProtectionStatus.AuthorizationRequired, DockGestureMode.Running, highRisk: true);
        Assert.Equal("待用 · 保护应用", state.ProfileLabel);
        Assert.Equal("受保护 · 未授权", state.ProtectionLabel);
        Assert.Equal(protectedProfile.ProfileId, Assert.Single(state.Options, option => option.Selected).ProfileId);
        var general = Assert.Single(state.Options, option => option.ProfileId is null);
        profiles.Select(general.ProfileId, general.AppId);
        Assert.Equal(global.ProfileId, profiles.ConfiguredFor("app-a").ProfileId);
        Assert.Equal(other.ProfileId, profiles.ConfiguredFor("app-b").ProfileId);
        Assert.Null(profiles.Resolve("app-a", ProtectionStatus.AuthorizationRequired));
    }
}
