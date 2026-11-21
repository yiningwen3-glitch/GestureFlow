namespace GestureFlow.Core;

public enum GestureInputKind
{
    Press = 1,
    Move,
    Release,
    Cancel
}

public readonly record struct GesturePoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public readonly record struct GestureInputEvent(GestureInputKind Kind, GesturePoint Position = default);

public interface IGestureInputSource
{
    event EventHandler<GestureInputEvent>? InputReceived;
}

public interface IActionExecutor
{
    void Execute(KeyboardShortcut shortcut);
}
