using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop.Applications;
using GestureFlow.Lab.Simulation;
using GestureFlow.Profiles;
using ApplicationIdentity = GestureFlow.Desktop.Applications.ApplicationIdentity;

namespace GestureFlow.Tests;

public sealed class ProtectionTests
{
    private const string SolidWorksPath = @"C:\CAD\sldworks.exe";

    [Theory]
    [InlineData("SLDWORKS.EXE")]
    [InlineData("acad.exe")]
    [InlineData("blender.exe")]
    [InlineData("Photoshop.exe")]
    [InlineData("Illustrator.exe")]
    [InlineData("Adobe Premiere Pro.exe")]
    [InlineData("WINWORD.EXE")]
    [InlineData("wps.exe")]
    [InlineData("Code.exe")]
    [InlineData("devenv.exe")]
    [InlineData("mstsc.exe")]
    [InlineData("FortniteClient-Win64-Shipping_EAC.exe")]
    public void ConservativeRulesIdentifyTheirScopeAndRecommendAnAlternateTrigger(string executable)
    {
        var assessment = new AppRiskPolicyService().Assess(executable);
        Assert.Equal(RiskLevel.High, assessment.Level);
        Assert.Equal(GestureTrigger.AltRightButtonDrag, assessment.RecommendedTrigger);
        Assert.NotEmpty(assessment.Reason);
        Assert.NotEmpty(assessment.Source);
        Assert.NotEmpty(assessment.Version);
    }

    [Theory]
    [InlineData("not-a-game.exe")]
    [InlineData("sldworks.exe-helper.exe")]
    [InlineData("unrecognized-remote-client.exe")]
    [InlineData(null)]
    public void UnmatchedRulesAreUnknownAndPassThroughByDefault(string? executable)
    {
        var risk = new AppRiskPolicyService().Assess(executable);
        Assert.Equal(RiskLevel.Unknown, risk.Level);
        var policy = new ProtectionPolicyService();
        var target = new ProtectionTarget("known-id", null, risk);
        Assert.Equal(ProtectedInputDecision.PassThrough,
            policy.EvaluateInput(target, null, false, GestureTrigger.RightButtonDrag, true));
        Assert.Equal(ProtectedInputDecision.PassThrough,
            policy.EvaluateInput(null, null, false, GestureTrigger.AltRightButtonDrag, true));
    }

    [Fact]
    public void ConfirmationCannotBeSkippedOrReused()
    {
        var target = Target();
        var flow = new AuthorizationFlow(target);
        Assert.Null(flow.Confirm(target));
        Assert.False(flow.Review(GestureTrigger.RightButtonDrag));
        Assert.Equal(ProtectionMeasures.None, flow.Measures);
        Assert.True(flow.AcknowledgeRisk());
        Assert.Equal(ProtectionPolicyService.RequiredMeasures, flow.Measures);
        Assert.Null(flow.Confirm(target));
        Assert.False(flow.Review((GestureTrigger)555));
        Assert.True(flow.Review(GestureTrigger.RightButtonDrag));
        var completed = Assert.IsType<CompletedAuthorization>(flow.Confirm(target));
        Assert.Equal(GestureTrigger.RightButtonDrag, completed.Data.Trigger);
        Assert.Null(flow.Confirm(target));
        Assert.True(completed.TryConsume());
        Assert.False(completed.TryConsume());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellingAtEitherConfirmationPreventsGrant(bool finalStep)
    {
        var target = Target();
        var flow = new AuthorizationFlow(target);
        if (finalStep)
        {
            Assert.True(flow.AcknowledgeRisk());
            Assert.True(flow.Review(GestureTrigger.MouseX2));
        }
        flow.Cancel();
        Assert.Equal(AuthorizationStep.Cancelled, flow.Step);
        Assert.False(flow.AcknowledgeRisk());
        Assert.Null(flow.Confirm(target));
    }

    [Fact]
    public void TargetOrRuleChangesInvalidatePendingAndExistingGrants()
    {
        var original = Target();
        var otherId = original with { AppId = ApplicationIdentity.ForPath(@"C:\Different\sldworks.exe") };
        var sameIdNewPath = original with { ExecutablePath = @"D:\CAD\sldworks.exe" };
        var changedRule = Target(new AppRiskPolicyService("updated-revision"));
        var missingLocation = original with { ExecutablePath = null };
        Assert.False(new AuthorizationFlow(missingLocation).AcknowledgeRisk());
        var grant = Complete(original).Data;
        foreach (var changed in new[] { otherId, sameIdNewPath, changedRule, missingLocation })
        {
            var flow = new AuthorizationFlow(original);
            flow.AcknowledgeRisk();
            flow.Review(GestureTrigger.AltRightButtonDrag);
            Assert.Null(flow.Confirm(changed));
            Assert.Equal(AuthorizationStep.Cancelled, flow.Step);
            Assert.False(ProtectionPolicyService.IsCurrent(changed, grant));
        }
    }

    [Theory]
    [InlineData(GestureTrigger.AltRightButtonDrag)]
    [InlineData(GestureTrigger.MouseX1)]
    [InlineData(GestureTrigger.MouseX2)]
    public void OnlyTheAuthorizedTriggerCanStartAndAbnormalInputCancelsWithoutExecuting(GestureTrigger trigger)
    {
        var target = Target();
        var grant = Complete(target, trigger).Data;
        var policy = new ProtectionPolicyService();
        Assert.Equal(ProtectedInputDecision.PassThrough, policy.EvaluateInput(target, grant, false, GestureTrigger.RightButtonDrag, true));
        Assert.Equal(ProtectedInputDecision.AllowGesture, policy.EvaluateInput(target, grant, false, trigger, true));
        Assert.Equal(ProtectedInputDecision.AllowGesture, policy.EvaluateInput(target, grant, false, null, true, target));
        Assert.Equal(ProtectedInputDecision.CancelGesture, policy.EvaluateInput(target, grant, false, null, false, target));
        Assert.Equal(ProtectedInputDecision.CancelGesture, policy.EvaluateInput(target, grant, false, trigger, true, target));
        Assert.Equal(ProtectedInputDecision.CancelGesture, policy.EvaluateInput(target, grant, false, null, true, target, foregroundChanged: true));
        Assert.Equal(ProtectedInputDecision.CancelGesture, policy.EvaluateInput(null, null, false, null, true, target));
    }

    [Fact]
    public void PauseIsTemporaryAndPermanentDisableOverridesAGrant()
    {
        var target = Target();
        var grant = Complete(target).Data;
        var service = new ProtectionPolicyService();
        Assert.Equal(ProtectionStatus.Ready, service.Evaluate(target, grant, false));
        service.SetPaused(true);
        Assert.Equal(ProtectionStatus.Paused, service.Evaluate(target, grant, false));
        Assert.Equal(ProtectionStatus.Disabled, service.Evaluate(target, grant, true));
        Assert.Equal(ProtectedInputDecision.CancelGesture, service.EvaluateInput(target, grant, false, null, true, target));
        service.SetPaused(false);
        Assert.Equal(ProtectionStatus.Ready, service.Evaluate(target, grant, false));
        Assert.Equal(ProtectedInputDecision.PassThrough, service.EvaluateInput(target, grant, true, grant.Trigger, true));
    }

    [Fact]
    public void EditingPolicyJsonCannotCreateChangeOrRestoreAProtectedGrant()
    {
        var folder = Path.Combine(Path.GetTempPath(), "GestureFlow-protection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var target = Target();
            var store = new ApplicationStateStore(folder);
            var json = Path.Combine(folder, "application-policies.json");
            var forged = $$"""
                {"version":1,"applications":{
                    "{{target.AppId}}":{"allowed":true,"riskLevel":"High","trigger":"RightButtonDrag","requiresProtection":true,"hasUserAuthorization":true}
                } }
                """;
            File.WriteAllText(json, forged);
            Assert.Throws<JsonException>(() => store.SavePolicy(target.AppId, store.LoadPolicies()[target.AppId]));
            Assert.Null(Verified(store, target));

            var finished = Complete(target);
            var virtualApp = VirtualApplicationScenarios.All[0];
            var virtualTarget = target with { AppId = virtualApp.AppId, ExecutablePath = virtualApp.ExecutablePath };
            Assert.Throws<JsonException>(() => store.SaveConfirmedPolicy(virtualTarget, Complete(virtualTarget)));
            Assert.Null(Verified(store, target));
            Assert.False(File.Exists(Path.Combine(folder, "application-authorizations.dat")));

            store.SaveConfirmedPolicy(target, finished);
            Assert.Throws<JsonException>(() => store.SaveConfirmedPolicy(target, finished));
            var genuinePolicy = File.ReadAllText(json);
            Assert.Equal(GestureTrigger.AltRightButtonDrag, Verified(store, target)!.Trigger);

            File.WriteAllText(json, forged);
            Assert.Null(Verified(store, target));
            var noProtection = JsonNode.Parse(genuinePolicy)!;
            noProtection["applications"]![target.AppId]!["requiresProtection"] = false;
            File.WriteAllText(json, noProtection.ToJsonString());
            Assert.Throws<JsonException>(() => store.LoadPolicies());

            File.WriteAllText(json, genuinePolicy);
            var disabled = store.LoadPolicies()[target.AppId] with { Allowed = false, HasUserAuthorization = false };
            store.SavePolicy(target.AppId, disabled);
            Assert.Empty(store.LoadAuthorizations());
            File.WriteAllText(json, genuinePolicy);
            Assert.Null(Verified(store, target));

            var ordinaryProfile = DefaultGestureProfiles.Create(GestureMode.Four) with
            {
                Applications = [new("sldworks.exe", true, true, true)]
            };
            Assert.Single(GestureProfileJson.Deserialize(GestureProfileJson.Serialize(ordinaryProfile)).Applications);
            Assert.Null(Verified(store, target));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static GestureAuthorization? Verified(ApplicationStateStore store, ProtectionTarget target) =>
        ApplicationGesturePolicy.VerifiedAuthorization(target, store.LoadPolicies().GetValueOrDefault(target.AppId), store.LoadAuthorizations());

    private static ProtectionTarget Target(AppRiskPolicyService? rules = null) =>
        new(ApplicationIdentity.ForPath(SolidWorksPath), SolidWorksPath, (rules ?? new AppRiskPolicyService()).Assess("sldworks.exe"));

    private static CompletedAuthorization Complete(ProtectionTarget target, GestureTrigger trigger = GestureTrigger.AltRightButtonDrag)
    {
        var flow = new AuthorizationFlow(target);
        Assert.True(flow.AcknowledgeRisk());
        Assert.True(flow.Review(trigger));
        return Assert.IsType<CompletedAuthorization>(flow.Confirm(target));
    }
}
