using GestureFlow.Core;
using GestureFlow.Profiles;

namespace GestureFlow.Lab.Simulation;

public readonly record struct TimedGestureInput(long Milliseconds, GestureInputKind Kind, double X = 0, double Y = 0)
{
    public GestureInputEvent ToGestureInput() => new(Kind, new(X, Y));
}

public enum GestureOutcome { None, Selected, Click, LongPress, DeadZone, Cancelled, Disabled }

public sealed record GesturePreview(GestureDirection Direction, string DisplayName, KeyboardShortcut Shortcut);

public sealed class PreviewActionExecutor : IActionExecutor
{
    public KeyboardShortcut? LastShortcut { get; private set; }

    public void Execute(KeyboardShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        if (!shortcut.IsValid())
            throw new ArgumentException("Invalid keyboard shortcut.", nameof(shortcut));

        LastShortcut = shortcut;
    }
}

public sealed record GestureTrace(TimedGestureInput Input, GestureUpdate Update, GesturePoint Displacement,
    double Distance, GestureBinding? MatchedBinding, GesturePreview? Preview, GestureOutcome Outcome);

public sealed class GestureLabSession
{
    private const long LongPressMilliseconds = 500;
    private readonly GestureProfileSession _machine;
    private readonly PreviewActionExecutor _executor = new();
    private GesturePoint _origin;
    private long _pressedAt;
    private bool _hasMoved;

    public GestureLabSession(GestureProfile profile)
    {
        Profile = profile;
        _machine = new(profile);
    }

    public GestureProfile Profile { get; }
    public GestureState State => _machine.State;

    public GestureTrace Process(TimedGestureInput timedInput)
    {
        var input = timedInput.ToGestureInput();
        var wasTracking = State == GestureState.Tracking;
        var (update, binding) = _machine.Process(input);
        if (!wasTracking && update.State == GestureState.Tracking)
        {
            _origin = input.Position;
            _pressedAt = timedInput.Milliseconds;
            _hasMoved = false;
        }

        var displacement = wasTracking && input.Position.IsFinite && input.Kind != GestureInputKind.Press
            ? new GesturePoint(input.Position.X - _origin.X, input.Position.Y - _origin.Y)
            : default;
        var distance = double.Hypot(displacement.X, displacement.Y);
        if (input.Kind == GestureInputKind.Move && distance > 0)
            _hasMoved = true;

        GesturePreview? preview = null;
        var outcome = GestureOutcome.None;
        if (input.Kind == GestureInputKind.Cancel)
            outcome = GestureOutcome.Cancelled;
        else if (update.CommittedDirection.HasValue)
        {
            if (binding is { Enabled: true })
            {
                _executor.Execute(binding.Shortcut);
                preview = new GesturePreview(binding.Direction, binding.DisplayName, _executor.LastShortcut!);
                outcome = GestureOutcome.Selected;
            }
            else
                outcome = GestureOutcome.Disabled;
        }
        else if (wasTracking && input.Kind == GestureInputKind.Release)
        {
            outcome = GestureOutcome.DeadZone;
            if (!_hasMoved && distance == 0 && input.Position.IsFinite)
                outcome = timedInput.Milliseconds - _pressedAt >= LongPressMilliseconds
                    ? GestureOutcome.LongPress : GestureOutcome.Click;
        }

        return new(timedInput, update, displacement, distance, binding, preview, outcome);
    }
}
