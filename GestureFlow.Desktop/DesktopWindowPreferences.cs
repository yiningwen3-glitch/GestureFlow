namespace GestureFlow.Desktop;

internal enum WindowCloseAction { Ask, Hide, Exit }

internal sealed record DesktopWindowPreferences(
    bool WelcomeSeen = false,
    WindowCloseAction CloseAction = WindowCloseAction.Ask);
