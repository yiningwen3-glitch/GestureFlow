using GestureFlow.Core;

namespace GestureFlow.Tests;

public sealed class DirectionRecognizerTests
{
    [Theory]
    [InlineData(0, -100, GestureDirection.North)]
    [InlineData(100, 0, GestureDirection.East)]
    [InlineData(0, 100, GestureDirection.South)]
    [InlineData(-100, 0, GestureDirection.West)]
    [InlineData(49, -50, GestureDirection.North)]
    [InlineData(50, -50, GestureDirection.East)]
    [InlineData(50, 50, GestureDirection.South)]
    [InlineData(-50, 50, GestureDirection.West)]
    [InlineData(-50, -50, GestureDirection.North)]
    public void FourDirectionsUseClockwiseSectors(int dx, int dy, GestureDirection expected)
    {
        var origin = new GesturePoint(300, -200);
        Assert.Equal(expected, DirectionRecognizer.Recognize(origin, new(origin.X + dx, origin.Y + dy), GestureMode.Four));
    }

    [Theory]
    [InlineData(0, -100, GestureDirection.North)]
    [InlineData(100, -100, GestureDirection.NorthEast)]
    [InlineData(100, 0, GestureDirection.East)]
    [InlineData(100, 100, GestureDirection.SouthEast)]
    [InlineData(0, 100, GestureDirection.South)]
    [InlineData(-100, 100, GestureDirection.SouthWest)]
    [InlineData(-100, 0, GestureDirection.West)]
    [InlineData(-100, -100, GestureDirection.NorthWest)]
    [InlineData(41, -100, GestureDirection.North)]
    [InlineData(42, -100, GestureDirection.NorthEast)]
    [InlineData(-41, -100, GestureDirection.North)]
    [InlineData(-42, -100, GestureDirection.NorthWest)]
    public void EightDirectionsIncludeDiagonalsAndWrapAroundNorth(int x, int y, GestureDirection expected) =>
        Assert.Equal(expected, DirectionRecognizer.Recognize(default, new(x, y), GestureMode.Eight));

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(23.999, 0, null)]
    [InlineData(24, 0, GestureDirection.East)]
    [InlineData(18, 18, GestureDirection.SouthEast)]
    public void UsesEuclideanDistanceWithAnInclusiveThreshold(double x, double y, GestureDirection? expected) =>
        Assert.Equal(expected, DirectionRecognizer.Recognize(default, new(x, y), GestureMode.Eight));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteCoordinatesHaveNoDirection(double invalid)
    {
        Assert.Null(DirectionRecognizer.Recognize(new(invalid, 0), new(30, 0), GestureMode.Four));
        Assert.Null(DirectionRecognizer.Recognize(default, new(0, invalid), GestureMode.Four));
    }

    [Fact]
    public void FiniteEndpointsCanSpanTheFullCoordinateRange() =>
        Assert.Equal(GestureDirection.SouthEast, DirectionRecognizer.Recognize(
            new(-double.MaxValue, -double.MaxValue), new(double.MaxValue, double.MaxValue), GestureMode.Eight));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidThresholdsAreRejected(double threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GestureStateMachine(GestureMode.Four, threshold));
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectionRecognizer.Recognize(default, default, GestureMode.Four, threshold));
    }

    [Fact]
    public void UnknownModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GestureStateMachine((GestureMode)5));
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectionRecognizer.Recognize(default, default, (GestureMode)5));
    }
}
