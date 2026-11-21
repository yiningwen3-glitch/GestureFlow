using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GestureFlow.Core;

namespace GestureFlow.UI;

public sealed class GestureSurface : Border, IGestureInputSource
{
    private Point _origin;
    private Point _lastPoint;
    public Point? DragAnchor { get; private set; }

    public event EventHandler<GestureInputEvent>? InputReceived;

    public void CancelDrag()
    {
        if (DragAnchor is null)
            return;

        Finish(GestureInputKind.Cancel, _lastPoint);
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (DragAnchor is not null || !CaptureMouse())
            return;

        Focus();
        _origin = _lastPoint = e.GetPosition(this);
        DragAnchor = _origin;
        Publish(GestureInputKind.Press, _origin);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (DragAnchor is not null)
            Publish(GestureInputKind.Move, e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (DragAnchor is null)
            return;

        Finish(GestureInputKind.Release, e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (DragAnchor is not null)
            Finish(GestureInputKind.Cancel, _lastPoint);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && DragAnchor is not null)
        {
            CancelDrag();
            e.Handled = true;
        }
    }

    private void Publish(GestureInputKind kind, Point point)
    {
        _lastPoint = point;
        var offset = point - _origin;
        InputReceived?.Invoke(this, new(kind, new(offset.X, offset.Y)));
    }

    private void Finish(GestureInputKind kind, Point point)
    {
        try { Publish(kind, point); }
        finally { DragAnchor = null; }
    }
}
