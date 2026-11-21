using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop.Applications;
using GestureFlow.Lab.Simulation;
using GestureFlow.Profiles;
using ApplicationIdentity = GestureFlow.Desktop.Applications.ApplicationIdentity;

namespace GestureFlow.Tests;

public sealed class ForegroundProfileTests
{
    [Fact]
    public void ForegroundChangesResolveMatchedProfilesOnlyAfterCurrentAuthorization()
    {
        var first = Application("sldworks.exe");
        var second = Application("editor.exe");
        var third = Application("browser.exe");
        var apps = new[] { first, second, third };
        var profiles = new GestureProfileManager();
        var global = profiles.Create("通用", select: true);
        var firstProfile = profiles.Create("设计", first.AppId, DefaultGestureProfiles.Create(GestureMode.Eight), select: true);
        var secondProfile = profiles.Create("编辑", second.AppId, select: true);
        var risks = new AppRiskPolicyService();
        var protection = new ProtectionPolicyService();
        var authorizations = new Dictionary<string, GestureAuthorization>();

        ForegroundGestureTarget Resolve(RunningApplication running) => ForegroundApplicationService.ResolveGestures(
            running, apps, risks, protection, profiles,
            (app, target) => ApplicationGesturePolicy.VerifiedAuthorization(target, app.UserOverride, authorizations));

        Assert.Equal(RiskLevel.High, first.RiskLevel);
        Assert.Equal(ProtectionStatus.AuthorizationRequired, Resolve(Window(first, 11)).Protection);
        Assert.Null(Resolve(Window(first, 11)).Profile);
        for (var index = 0; index < apps.Length; index++)
        {
            var app = apps[index];
            var risk = risks.Assess(app.ExecutableName);
            var target = new ProtectionTarget(app.AppId, app.ExecutablePath, risk, app.InstallationStamp);
            var flow = new AuthorizationFlow(target);
            Assert.True(flow.AcknowledgeRisk());
            Assert.True(flow.Review(GestureTrigger.AltRightButtonDrag));
            authorizations[app.AppId] = flow.Confirm(target)!.Data;
            apps[index] = app with { UserOverride = new(true, risk.Level, GestureTrigger.AltRightButtonDrag, true, true) };
        }

        var captured = Resolve(Window(first, 11)).Profile!;
        Assert.Equal(firstProfile.ProfileId, captured.ProfileId);
        Assert.Equal(secondProfile.ProfileId, Resolve(Window(second, 12)).Profile!.ProfileId);
        Assert.Equal(global.ProfileId, Resolve(Window(third, 13)).Profile!.ProfileId);
        profiles.Edit(captured.ProfileId, DefaultGestureProfiles.Create(GestureMode.Four));
        Assert.Equal(GestureMode.Eight, captured.Configuration.Mode);
        Assert.Equal(GestureMode.Four, Resolve(Window(first, 11)).Profile!.Configuration.Mode);
        Assert.Null(Resolve(Window(first, 11) with { InstallationStamp = "replaced" }).Profile);
        Assert.Null(Resolve(Window(first, 11) with { WindowKind = ForegroundWindowKind.ApplicationHost }).Profile);

        var authorized = apps[0];
        apps[0] = authorized with { UserOverride = authorized.UserOverride! with { Allowed = false } };
        Assert.Equal(ProtectionStatus.Disabled, Resolve(Window(first, 11)).Protection);
        Assert.Null(Resolve(Window(first, 11)).Profile);
        protection.SetPaused(true);
        Assert.Equal(ProtectionStatus.Paused, Resolve(Window(second, 12)).Protection);
        Assert.Null(Resolve(Window(second, 12)).Profile);
    }

    [Fact]
    public void LabAssemblyHasNoDesktopReferenceOrNativeImport()
    {
        var assembly = typeof(PreviewActionExecutor).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => name.Name == "GestureFlow.Desktop");
        Assert.DoesNotContain(assembly.GetTypes().SelectMany(type => type.GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.DeclaredOnly)), method =>
            (method.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0);
    }

    private static InstalledApplication Application(string executable)
    {
        var path = $@"C:\Apps\{executable}";
        return new(ApplicationIdentity.ForPath(path), executable, executable, path, null, null, null,
            DiscoverySource.Manual, IdentificationStatus.Matched, false,
            new AppRiskPolicyService().Assess(executable).Level, null, "installation");
    }

    private static RunningApplication Window(InstalledApplication app, int processId) =>
        new(processId + 100, processId, processId, "Window", ForegroundWindowKind.DesktopProgram,
            app.ExecutableName, app.ExecutablePath, null, null, app.InstallationStamp);
}
