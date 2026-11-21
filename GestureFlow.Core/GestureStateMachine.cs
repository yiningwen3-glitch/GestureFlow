namespace GestureFlow.Core;

public enum GestureState
{
    Idle,
    Tracking
}

public readonly record struct GestureUpdate(
    GestureState State, GestureDirection? Candidate, GestureDirection? CommittedDirection);

public sealed class GestureStateMachine
{
    private readonly GestureMode _mode;
    private readonly double _minimumDistance;
    private GesturePoint _origin;

    public GestureStateMachine(GestureMode mode, double minimumDistance = GestureWheelMetrics.DefaultTriggerDistance)
    {
        DirectionRecognizer.GetDirections(mode);
        DirectionRecognizer.ValidateMinimumDistance(minimumDistance);
        _mode = mode;
        _minimumDistance = minimumDistance;
    }

    public GestureState State { get; private set; }
    public GestureDirection? Candidate { get; private set; }

    public GestureUpdate Process(GestureInputEvent input)
    {
        GestureDirection? committed = null;
        switch (input.Kind)
        {
            case GestureInputKind.Press when State == GestureState.Idle && input.Position.IsFinite:
                _origin = input.Position;
                State = GestureState.Tracking;
                break;
            case GestureInputKind.Move when State == GestureState.Tracking && input.Position.IsFinite:
                Candidate = Recognize(input.Position);
                break;
            case GestureInputKind.Release when State == GestureState.Tracking:
                committed = Recognize(input.Position);
                Reset();
                break;
            case GestureInputKind.Cancel:
                Reset();
                break;
        }

        return new(State, Candidate, committed);
    }

    private GestureDirection? Recognize(GesturePoint position) =>
        DirectionRecognizer.Recognize(_origin, position, _mode, _minimumDistance);

    private void Reset()
    {
        State = GestureState.Idle;
        Candidate = null;
    }
}
