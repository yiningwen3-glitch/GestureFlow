using GestureFlow.Core;

namespace GestureFlow.Profiles;

public class GestureProfileEditor
{
    private Dictionary<GestureDirection, GestureBinding> _bindings = new();

    public GestureProfileEditor(GestureProfile profile) => Load(profile);

    public GestureProfile Profile { get; private set; } = null!;

    public void Load(GestureProfile profile)
    {
        var errors = GestureProfileValidator.Validate(profile);
        if (errors.Count > 0)
            throw new ProfileValidationException(errors);

        _bindings = DefaultGestureProfiles.Create(GestureMode.Eight).Bindings.ToDictionary(item => item.Direction);
        foreach (var binding in profile.Bindings)
            _bindings[binding.Direction] = binding;
        Profile = profile with { Applications = Array.AsReadOnly(profile.Applications.ToArray()) };
        Refresh(profile.Mode);
    }

    public void ChangeMode(GestureMode mode) => Refresh(mode);

    public bool SetName(GestureDirection direction, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        _bindings[direction] = _bindings[direction] with { DisplayName = name };
        Refresh(Profile.Mode);
        return true;
    }

    public bool SetShortcut(GestureDirection direction, KeyboardShortcut shortcut)
    {
        if (!shortcut.IsValid())
            return false;

        _bindings[direction] = _bindings[direction] with { Shortcut = shortcut };
        Refresh(Profile.Mode);
        return true;
    }

    public void RestoreDefaults() => Load(DefaultGestureProfiles.Create(Profile.Mode));

    private void Refresh(GestureMode mode)
    {
        var bindings = DirectionRecognizer.GetDirections(mode).Select(direction => _bindings[direction]).ToArray();
        Profile = Profile with { Mode = mode, Bindings = Array.AsReadOnly(bindings) };
    }
}
