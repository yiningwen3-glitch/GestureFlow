namespace GestureFlow.Core.Protection;

[Flags]
public enum ProtectionMeasures
{
    None = 0, EmergencyPause = 1, CancelOnInvalidInput = 2, CancelOnApplicationSwitch = 4
}

public sealed record ProtectionTarget(string AppId, string? ExecutablePath, AppRiskAssessment Risk, string? InstallationStamp = null)
{
    public bool CanAuthorize => !string.IsNullOrWhiteSpace(AppId)
        && (Risk.Level != RiskLevel.High || !string.IsNullOrWhiteSpace(ExecutablePath));
}

public sealed record GestureAuthorization(string AppId, string? ExecutablePath, string PolicyStamp,
    GestureTrigger Trigger, ProtectionMeasures Measures, string? InstallationStamp = null);

public enum ProtectionStatus { Unmatched, Disabled, AuthorizationRequired, Paused, Ready }
public enum ProtectedInputDecision { PassThrough, AllowGesture, CancelGesture }

public sealed class ProtectionPolicyService
{
    public const ProtectionMeasures RequiredMeasures = ProtectionMeasures.EmergencyPause
        | ProtectionMeasures.CancelOnInvalidInput | ProtectionMeasures.CancelOnApplicationSwitch;
    public bool IsPaused { get; private set; }

    public void SetPaused(bool paused) => IsPaused = paused;

    public static bool IsCurrent(ProtectionTarget? target, GestureAuthorization? authorization) =>
        target is { CanAuthorize: true } && authorization is not null
        && string.Equals(target.AppId, authorization.AppId, StringComparison.Ordinal)
        && string.Equals(target.ExecutablePath, authorization.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && target.InstallationStamp == authorization.InstallationStamp
        && target.Risk.PolicyStamp == authorization.PolicyStamp && Enum.IsDefined(authorization.Trigger)
        && authorization.Measures == RequiredMeasures;

    public ProtectionStatus Evaluate(ProtectionTarget? target, GestureAuthorization? verifiedAuthorization, bool permanentlyDisabled)
    {
        if (permanentlyDisabled)
            return ProtectionStatus.Disabled;
        if (target is null)
            return ProtectionStatus.Unmatched;
        if (!IsCurrent(target, verifiedAuthorization))
            return ProtectionStatus.AuthorizationRequired;
        return IsPaused ? ProtectionStatus.Paused : ProtectionStatus.Ready;
    }

    public ProtectedInputDecision EvaluateInput(ProtectionTarget? foreground, GestureAuthorization? verifiedAuthorization,
        bool permanentlyDisabled, GestureTrigger? observedTrigger, bool inputIsValid,
        ProtectionTarget? activeGesture = null, bool foregroundChanged = false)
    {
        var ready = Evaluate(foreground, verifiedAuthorization, permanentlyDisabled) == ProtectionStatus.Ready;
        if (activeGesture is not null)
            return !ready || !inputIsValid || observedTrigger is not null || foregroundChanged || !IsCurrent(activeGesture, verifiedAuthorization)
                ? ProtectedInputDecision.CancelGesture : ProtectedInputDecision.AllowGesture;

        return ready && inputIsValid && observedTrigger == verifiedAuthorization!.Trigger
            ? ProtectedInputDecision.AllowGesture : ProtectedInputDecision.PassThrough;
    }
}

public enum AuthorizationStep { RiskNotice, ProtectionSettings, FinalConfirmation, Completed, Cancelled }

public sealed class CompletedAuthorization
{
    private int _consumed;

    internal CompletedAuthorization(GestureAuthorization data) => Data = data;

    public GestureAuthorization Data { get; }

    public bool TryConsume() => Interlocked.Exchange(ref _consumed, 1) == 0;
}

public sealed class AuthorizationFlow(ProtectionTarget target)
{
    public AuthorizationStep Step { get; private set; } = AuthorizationStep.RiskNotice;
    public GestureTrigger Trigger { get; private set; } = target.Risk.RecommendedTrigger;
    public ProtectionMeasures Measures { get; private set; }

    public bool AcknowledgeRisk()
    {
        if (Step != AuthorizationStep.RiskNotice || !target.CanAuthorize)
            return false;
        Measures = ProtectionPolicyService.RequiredMeasures;
        Step = AuthorizationStep.ProtectionSettings;
        return true;
    }

    public bool Review(GestureTrigger trigger)
    {
        if (Step != AuthorizationStep.ProtectionSettings || !Enum.IsDefined(trigger))
            return false;
        Trigger = trigger;
        Step = AuthorizationStep.FinalConfirmation;
        return true;
    }

    public CompletedAuthorization? Confirm(ProtectionTarget current)
    {
        if (Step != AuthorizationStep.FinalConfirmation)
            return null;

        var authorization = new GestureAuthorization(target.AppId, target.ExecutablePath,
            target.Risk.PolicyStamp, Trigger, Measures, target.InstallationStamp);
        if (!ProtectionPolicyService.IsCurrent(current, authorization))
        {
            Cancel();
            return null;
        }

        Step = AuthorizationStep.Completed;
        return new CompletedAuthorization(authorization);
    }

    public void Cancel()
    {
        if (Step != AuthorizationStep.Completed)
            Step = AuthorizationStep.Cancelled;
    }
}
