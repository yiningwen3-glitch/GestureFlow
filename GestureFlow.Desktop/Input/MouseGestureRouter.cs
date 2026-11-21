using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Profiles;

namespace GestureFlow.Desktop.Input;

internal enum MouseButton { None, Right, X1, X2, Other, Left }
internal enum MouseSignal { Move, Down, Up, Other }

internal sealed record InputPermission(nint Window, nint KeyboardFocus, uint ProcessId, long FocusEpoch, long Revision,
    long ExpiresAt, string AppId, GestureTrigger Trigger, GestureProfile Profile, WindowsInputNative.Rect Bounds,
    string? ExecutablePath = null, string? InstallationStamp = null, bool IsDesktop = false)
{
    public bool SameTarget(InputPermission? other) => other is not null && Window == other.Window
        && KeyboardFocus == other.KeyboardFocus && ProcessId == other.ProcessId && FocusEpoch == other.FocusEpoch && Revision == other.Revision
        && AppId == other.AppId && Trigger == other.Trigger && InstallationStamp == other.InstallationStamp && IsDesktop == other.IsDesktop
        && ReferenceEquals(Profile, other.Profile);

    public bool Contains(GesturePoint point) => point.X >= Bounds.Left && point.X < Bounds.Right
        && point.Y >= Bounds.Top && point.Y < Bounds.Bottom;
}

internal readonly record struct MousePacket(MouseSignal Signal, MouseButton Button, GesturePoint Position, long Tick,
    ShortcutModifiers Modifiers, bool Injected = false, bool OtherButtonHeld = false);

internal readonly record struct CapturedInput(GestureInputEvent Input, InputPermission Permission);

internal sealed class MouseGestureRouter(Action<CapturedInput> publish)
{
    private const long MaximumGestureMilliseconds = 5000;
    private MouseButton _ownedButton;
    private InputPermission? _active;
    private GesturePoint _origin;
    private long _started;

    public bool OwnsButton => _ownedButton != MouseButton.None;

    internal static MouseButton TriggerButton(GestureTrigger trigger) => trigger switch
    {
        GestureTrigger.MouseX1 => MouseButton.X1,
        GestureTrigger.MouseX2 => MouseButton.X2,
        _ => MouseButton.None
    };

    internal static bool MatchesTrigger(GestureTrigger trigger, MousePacket packet) =>
        TriggerButton(trigger) is var button && button != MouseButton.None
        && packet.Button == button && packet.Modifiers == ShortcutModifiers.None;

    public bool Route(MousePacket packet, InputPermission? permission, bool pointerOverTarget)
    {
        if (packet.Injected)
            return false;

        Advance(packet.Tick, permission);

        if (_ownedButton != MouseButton.None)
        {
            if (packet.Signal == MouseSignal.Up && packet.Button == _ownedButton)
            {
                _ownedButton = MouseButton.None;
                if (_active is { } finished)
                {
                    _active = null;
                    Emit(GestureInputKind.Release, packet.Position, finished);
                }
                return true;
            }

            if (packet.Signal == MouseSignal.Down)
            {
                Cancel();
                return packet.Button == _ownedButton;
            }

            if (packet.Signal == MouseSignal.Move && _active is { } moving)
                Emit(GestureInputKind.Move, packet.Position, moving);
            else if (packet.Signal == MouseSignal.Other)
                Cancel();

            return false;
        }

        if (packet.Signal != MouseSignal.Down || permission is null || !pointerOverTarget || packet.OtherButtonHeld
            || !MatchesTrigger(permission.Trigger, packet))
            return false;

        _active = permission;
        _ownedButton = packet.Button;
        _origin = packet.Position;
        _started = packet.Tick;
        Emit(GestureInputKind.Press, packet.Position, permission);
        return true;
    }

    public void Advance(long tick, InputPermission? permission)
    {
        if (_active is not { } active)
            return;
        if (!active.SameTarget(permission) || tick - _started > MaximumGestureMilliseconds)
            Cancel();
    }

    public void Reset()
    {
        Cancel();
        _ownedButton = MouseButton.None;
    }

    public void Cancel()
    {
        if (_active is not { } active)
            return;

        _active = null;
        Emit(GestureInputKind.Cancel, _origin, active);
    }

    private void Emit(GestureInputKind kind, GesturePoint point, InputPermission permission) =>
        publish(new(new GestureInputEvent(kind, point), permission));
}
