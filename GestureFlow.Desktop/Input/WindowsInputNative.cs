using System.Runtime.InteropServices;
using GestureFlow.Core;
using GestureFlow.Desktop.Applications;

namespace GestureFlow.Desktop.Input;

internal static class WindowsInputNative
{
    internal static readonly nuint InjectionMarker = (nuint)(0x47534600u ^ (uint)Environment.ProcessId);
    internal const uint WmHotkey = 0x0312;

    [StructLayout(LayoutKind.Sequential)]
    internal record struct Point(int X, int Y)
    {
        public GesturePoint ToGesturePoint() => new(X, Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort Key, ScanCode;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint ExtraInfo;
    }

    internal static bool IsDown(int key) => GetAsyncKeyState(key) < 0;

    internal static ShortcutModifiers HeldModifiers()
    {
        var modifiers = ShortcutModifiers.None;
        if (IsDown(0x11)) modifiers |= ShortcutModifiers.Control;
        if (IsDown(0x10)) modifiers |= ShortcutModifiers.Shift;
        if (IsDown(0x12)) modifiers |= ShortcutModifiers.Alt;
        if (IsDown(0x5B) || IsDown(0x5C)) modifiers |= ShortcutModifiers.Windows;
        return modifiers;
    }

    internal static bool IsForeground(InputPermission permission) => GetForegroundWindow() == permission.Window
        && GetWindowThreadProcessId(permission.Window, out var pid) != 0 && pid == permission.ProcessId;

    internal static nint StableKeyboardFocus(nint window)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info) || info.Active != window || (info.Flags & 0x1E) != 0)
            return 0;
        // Some foreground surfaces have no separately focused child control.
        return info.Focus != 0 ? info.Focus : info.Active;
    }

    internal static bool CanSendToTarget(uint windowOwner, nint focus) =>
        GetWindowThreadProcessId(focus, out var focusOwner) != 0
        && WindowsProcessQuery.CanSendInputTo(windowOwner)
        && (focusOwner == windowOwner || WindowsProcessQuery.CanSendInputTo(focusOwner));

    internal static bool IsOverTarget(Point point, InputPermission permission)
    {
        var hit = WindowFromPoint(point);
        if (hit == 0)
            return false;

        var root = GetAncestor(hit, 2);
        return permission.IsDesktop
            ? ForegroundApplicationService.IsDesktopSurface(root, permission.ProcessId)
            : root == permission.Window;
    }

    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out Rect bounds);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor, int kind, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowStyle(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowStyle(nint hwnd, int index, nint style);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RegisterWindowMessage(string message);
}
