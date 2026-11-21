namespace GestureFlow.Core;

public enum GestureMode
{
    Four = 4,
    Eight = 8
}

public enum GestureDirection
{
    North, NorthEast, East, SouthEast, South, SouthWest, West, NorthWest
}

public static class DirectionRecognizer
{
    public const double DefaultMinimumDistance = 24;

    private static readonly IReadOnlyList<GestureDirection> FourDirections =
        Array.AsReadOnly(new[] { GestureDirection.North, GestureDirection.East, GestureDirection.South, GestureDirection.West });

    private static readonly IReadOnlyList<GestureDirection> EightDirections =
        Array.AsReadOnly(Enum.GetValues<GestureDirection>());

    public static IReadOnlyList<GestureDirection> GetDirections(GestureMode mode) => mode switch
    {
        GestureMode.Four => FourDirections,
        GestureMode.Eight => EightDirections,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static GestureDirection? Recognize(GesturePoint origin, GesturePoint position, GestureMode mode,
        double minimumDistance = DefaultMinimumDistance)
    {
        var directions = GetDirections(mode);
        ValidateMinimumDistance(minimumDistance);
        if (!origin.IsFinite || !position.IsFinite)
            return null;

        var dx = position.X - origin.X;
        var dy = position.Y - origin.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
        {
            // Scale both axes equally when subtracting extreme, finite endpoints would overflow.
            dx = position.X / 2 - origin.X / 2;
            dy = position.Y / 2 - origin.Y / 2;
            minimumDistance /= 2;
        }

        if ((dx == 0 && dy == 0) || double.Hypot(dx, dy) < minimumDistance)
            return null;

        // Windows coordinates grow downward. Angles increase clockwise from north.
        var sectorWidth = Math.Tau / directions.Count;
        var angle = Math.Atan2(dx, -dy);
        var sector = (int)Math.Floor(angle / sectorWidth + 0.5);
        return directions[(sector + directions.Count) % directions.Count];
    }

    internal static void ValidateMinimumDistance(double minimumDistance)
    {
        if (!double.IsFinite(minimumDistance) || minimumDistance <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumDistance));
    }
}
