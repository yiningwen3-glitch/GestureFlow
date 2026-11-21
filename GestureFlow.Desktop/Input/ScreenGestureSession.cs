using GestureFlow.Core;
using GestureFlow.Profiles;

namespace GestureFlow.Desktop.Input;

internal sealed class ScreenGestureSession(GestureProfile profile, GesturePoint origin, double pixelsPerDip,
    double minimumDistance = GestureWheelMetrics.DefaultTriggerDistance)
{
    private readonly GestureProfileSession _session = new(profile, minimumDistance);

    public GestureDirection? Candidate => _session.Candidate;

    public (GestureUpdate Update, GestureBinding? Binding) Process(GestureInputEvent input)
    {
        var offset = new GesturePoint((input.Position.X - origin.X) / pixelsPerDip, (input.Position.Y - origin.Y) / pixelsPerDip);
        return _session.Process(input with { Position = offset });
    }
}
