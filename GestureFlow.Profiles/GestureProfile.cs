using System.Text.Json.Serialization;
using GestureFlow.Core;

namespace GestureFlow.Profiles;

public sealed record GestureBinding(
    [property: JsonRequired] GestureDirection Direction,
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] KeyboardShortcut Shortcut,
    [property: JsonRequired] bool Enabled);

public sealed record ApplicationRule(
    [property: JsonRequired] string ExecutableName,
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] bool PotentialRightClickConflict,
    [property: JsonRequired] bool RightClickConflictConfirmed);

public enum ApplicationGestureStatus
{
    Disabled,
    ConfirmationRequired,
    Enabled
}

public sealed record GestureProfile(
    [property: JsonRequired] GestureMode Mode,
    [property: JsonRequired] IReadOnlyList<GestureBinding> Bindings,
    [property: JsonRequired] IReadOnlyList<ApplicationRule> Applications)
{
    public GestureBinding? GetEnabledBinding(GestureDirection direction) =>
        Bindings.FirstOrDefault(binding => binding.Direction == direction && binding.Enabled);

    public ApplicationGestureStatus GetApplicationStatus(string? executableName, bool potentialRightClickConflict = false)
    {
        var rule = Applications.FirstOrDefault(application =>
            string.Equals(application.ExecutableName, executableName, StringComparison.OrdinalIgnoreCase));
        if (rule is not { Enabled: true })
            return ApplicationGestureStatus.Disabled;

        var requiresConfirmation = potentialRightClickConflict || rule.PotentialRightClickConflict;
        var confirmed = rule.PotentialRightClickConflict && rule.RightClickConflictConfirmed;
        return requiresConfirmation && !confirmed
            ? ApplicationGestureStatus.ConfirmationRequired
            : ApplicationGestureStatus.Enabled;
    }
}
