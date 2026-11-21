using GestureFlow.Core;

namespace GestureFlow.Profiles;

public static class GestureProfileValidator
{
    public static IReadOnlyList<string> Validate(GestureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = new List<string>();
        IReadOnlyList<GestureDirection> allowed = Array.Empty<GestureDirection>();
        if (Enum.IsDefined(profile.Mode))
            allowed = DirectionRecognizer.GetDirections(profile.Mode);
        else
            errors.Add("mode: expected Four or Eight.");

        ValidateBindings(profile.Bindings, allowed, errors);
        ValidateApplications(profile.Applications, errors);
        return errors.AsReadOnly();
    }

    private static void ValidateBindings(IReadOnlyList<GestureBinding>? bindings,
        IReadOnlyList<GestureDirection> allowed, List<string> errors)
    {
        if (bindings is null)
        {
            errors.Add("bindings: an array is required.");
            return;
        }

        var seen = new HashSet<GestureDirection>();
        for (var index = 0; index < bindings.Count; index++)
        {
            var path = $"bindings[{index}]";
            var binding = bindings[index];
            if (binding is null)
            {
                errors.Add($"{path}: a binding is required.");
                continue;
            }

            if (!allowed.Contains(binding.Direction))
                errors.Add($"{path}.direction: direction is unavailable in this mode.");
            else if (!seen.Add(binding.Direction))
                errors.Add($"{path}.direction: duplicate direction.");

            if (string.IsNullOrWhiteSpace(binding.DisplayName))
                errors.Add($"{path}.displayName: a display name is required.");
            if (binding.Shortcut?.IsValid() != true)
                errors.Add($"{path}.shortcut: a supported key and modifier combination is required.");
        }

        foreach (var direction in allowed.Where(direction => !seen.Contains(direction)))
            errors.Add($"bindings: missing direction {direction}.");
    }

    private static void ValidateApplications(IReadOnlyList<ApplicationRule>? applications, List<string> errors)
    {
        if (applications is null)
        {
            errors.Add("applications: an array is required.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < applications.Count; index++)
        {
            var path = $"applications[{index}]";
            var rule = applications[index];
            if (rule is null)
            {
                errors.Add($"{path}: a rule is required.");
                continue;
            }

            if (!IsExecutableName(rule.ExecutableName))
                errors.Add($"{path}.executableName: expected a Windows .exe file name.");
            else if (!seen.Add(rule.ExecutableName))
                errors.Add($"{path}.executableName: duplicate application.");

            if (rule is { Enabled: true, PotentialRightClickConflict: true, RightClickConflictConfirmed: false })
                errors.Add($"{path}: enabling this application requires right-click conflict confirmation.");
            if (rule is { PotentialRightClickConflict: false, RightClickConflictConfirmed: true })
                errors.Add($"{path}: conflict confirmation requires a recorded right-click conflict.");
        }
    }

    private static bool IsExecutableName(string? name) =>
        name is { Length: > 4 }
        && name == name.Trim()
        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) < 0
        && !name.Any(char.IsControl);
}
