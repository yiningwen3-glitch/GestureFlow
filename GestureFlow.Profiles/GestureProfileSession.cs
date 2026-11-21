using GestureFlow.Core;

namespace GestureFlow.Profiles;

public sealed class GestureProfileSession
{
    private readonly GestureProfile _profile;
    private readonly GestureStateMachine _machine;

    public GestureProfileSession(GestureProfile profile) : this(profile, GestureWheelMetrics.DefaultTriggerDistance) { }

    public GestureProfileSession(GestureProfile profile, double minimumDistance)
    {
        _profile = profile;
        _machine = new(profile.Mode, minimumDistance);
    }

    public GestureState State => _machine.State;
    public GestureDirection? Candidate => _machine.Candidate;

    public (GestureUpdate Update, GestureBinding? Binding) Process(GestureInputEvent input)
    {
        var update = _machine.Process(input);
        var direction = update.CommittedDirection ?? update.Candidate;
        var binding = direction is { } value ? _profile.Bindings.FirstOrDefault(item => item.Direction == value) : null;
        return (update, binding);
    }
}
