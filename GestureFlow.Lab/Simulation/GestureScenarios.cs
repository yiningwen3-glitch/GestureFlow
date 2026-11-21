using GestureFlow.Core;

namespace GestureFlow.Lab.Simulation;

public enum GestureScenario
{
    Click, LongPress, Invalid, Cancel, Rapid
}

public static class GestureScenarios
{
    public static IReadOnlyList<TimedGestureInput> Direction(GestureDirection direction)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));

        var angle = (int)direction * Math.Tau / 8;
        // Snap cardinal axes to zero and diagonals to identical magnitudes for exact sector boundaries.
        var x = Math.Round(Math.Sin(angle) * 100);
        var y = Math.Round(-Math.Cos(angle) * 100);
        return
        [
            new(0, GestureInputKind.Press),
            new(65, GestureInputKind.Move, x * 0.4, y * 0.4),
            new(125, GestureInputKind.Move, x * 0.75, y * 0.75),
            new(190, GestureInputKind.Release, x, y)
        ];
    }

    public static IReadOnlyList<TimedGestureInput> Create(GestureScenario scenario) => scenario switch
    {
        GestureScenario.Click => [new(0, GestureInputKind.Press), new(70, GestureInputKind.Release)],
        GestureScenario.LongPress => [new(0, GestureInputKind.Press), new(850, GestureInputKind.Release)],
        GestureScenario.Invalid =>
            [new(0, GestureInputKind.Press), new(80, GestureInputKind.Move, 85), new(150, GestureInputKind.Release, 9, 4)],
        GestureScenario.Cancel =>
            [new(0, GestureInputKind.Press), new(80, GestureInputKind.Move, 65), new(150, GestureInputKind.Cancel, 65)],
        GestureScenario.Rapid =>
        [
            new(0, GestureInputKind.Press), new(30, GestureInputKind.Move, 0, -90), new(55, GestureInputKind.Release, 0, -90),
            new(75, GestureInputKind.Press), new(105, GestureInputKind.Move, 75), new(130, GestureInputKind.Release, 90),
            new(150, GestureInputKind.Press), new(180, GestureInputKind.Move, 0, 75), new(205, GestureInputKind.Release, 0, 90)
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };
}
