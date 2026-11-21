using GestureFlow.Core;

namespace GestureFlow.Tests;

public sealed class GestureStateMachineTests
{
    private readonly GestureStateMachine _machine = new(GestureMode.Eight);
    private const double Threshold = GestureWheelMetrics.DefaultTriggerDistance;

    [Fact]
    public void BoundaryToleranceAndReleaseNeverExecuteFromTheCenter()
    {
        Assert.Equal(new(GestureState.Tracking, null, null), Send(GestureInputKind.Press, 40, 60));
        Assert.Equal(new(GestureState.Tracking, null, null), Send(GestureInputKind.Move, 40 + Threshold - 0.1, 60));
        Assert.Equal(new(GestureState.Idle, null, null), Send(GestureInputKind.Release, 40, 60));
    }

    [Fact]
    public void MovementChangesThePreviewAndReleaseCommitsExactlyOnce()
    {
        Send(GestureInputKind.Press, 100, 100);
        Send(GestureInputKind.Press, 300, 300);
        Assert.Equal(new(GestureState.Tracking, GestureDirection.East, null), Send(GestureInputKind.Move, 100 + Threshold, 100));
        Assert.Equal(new(GestureState.Tracking, GestureDirection.North, null), Send(GestureInputKind.Move, 100, -100));
        Assert.Equal(new(GestureState.Idle, null, GestureDirection.South), Send(GestureInputKind.Release, 100, 250));
        Assert.Null(Send(GestureInputKind.Release, 100, 250).CommittedDirection);
        Assert.Equal(GestureState.Idle, _machine.State);
        Send(GestureInputKind.Press, 200, 200);
        Assert.Equal(GestureDirection.South, Send(GestureInputKind.Release, 200, 300).CommittedDirection);
    }

    [Fact]
    public void CancelResetsAnUncommittedGesture()
    {
        Send(GestureInputKind.Press);
        Send(GestureInputKind.Move, 6, 0);
        Assert.Equal(new(GestureState.Idle, null, null), Send(GestureInputKind.Cancel, double.NaN));
        Assert.Null(Send(GestureInputKind.Move, 200, 0).CommittedDirection);
    }

    [Theory]
    [InlineData(GestureInputKind.Move)]
    [InlineData(GestureInputKind.Release)]
    [InlineData(GestureInputKind.Cancel)]
    [InlineData((GestureInputKind)0)]
    public void EventsWithoutAValidPressAreIgnored(GestureInputKind kind) =>
        Assert.Equal(new(GestureState.Idle, null, null), Send(kind, 100));

    [Fact]
    public void InvalidCoordinatesCannotStartOrCommitAGesture()
    {
        Assert.Equal(GestureState.Idle, Send(GestureInputKind.Press, double.NaN).State);
        Send(GestureInputKind.Press);
        Assert.Null(Send(GestureInputKind.Move, double.PositiveInfinity).CommittedDirection);
        Assert.Equal(new(GestureState.Idle, null, null), Send(GestureInputKind.Release, double.NegativeInfinity));
    }

    private GestureUpdate Send(GestureInputKind kind, double x = 0, double y = 0) =>
        _machine.Process(new(kind, new(x, y)));
}
