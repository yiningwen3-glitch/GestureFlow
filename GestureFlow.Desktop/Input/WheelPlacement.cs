using GestureFlow.Core;

namespace GestureFlow.Desktop.Input;

internal readonly record struct WheelPlacement(int X, int Y, int Size, double Scale)
{
    internal const int WheelSize = GestureWheelMetrics.OuterDiameter + 4;

    public static WheelPlacement Around(WindowsInputNative.Point point, uint dpi, WindowsInputNative.Rect? workArea)
    {
        var scale = dpi == 0 ? 1 : dpi / 96.0;
        var size = (int)Math.Round(WheelSize * scale);
        if (workArea is { } work && work.Right > work.Left && work.Bottom > work.Top)
        {
            size = Math.Min(size, Math.Min(work.Right - work.Left, work.Bottom - work.Top));
            return new(Math.Clamp(point.X - size / 2, work.Left, work.Right - size),
                Math.Clamp(point.Y - size / 2, work.Top, work.Bottom - size), size, scale);
        }

        return new(point.X - size / 2, point.Y - size / 2, size, scale);
    }
}
