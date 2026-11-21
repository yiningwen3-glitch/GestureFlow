using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using GestureFlow.Core;
using GestureFlow.Core.Protection;

namespace GestureFlow.Desktop.Input;

internal sealed class WindowsGestureInputSource : IDisposable
{
    private const uint StopMessage = 0x8001;
    private const int QueueLimit = 1024;
    private readonly ConcurrentQueue<CapturedInput> _events = new();
    private readonly MouseGestureRouter _router;
    private readonly MouseHook _mouseHook;
    private readonly WindowEvent _windowEvent;
    private readonly object _lifecycle = new();
    private Thread? _thread;
    private uint _threadId;
    private InputPermission? _permission;
    private int _queued;
    private int _faulted;
    private long _focusEpoch;
    private int _running;
    private int _stopRequested;
    private bool _disposed;

    public WindowsGestureInputSource()
    {
        _router = new MouseGestureRouter(Enqueue);
        _mouseHook = OnMouse;
        _windowEvent = OnForegroundChanged;
    }

    internal long FocusEpoch => Interlocked.Read(ref _focusEpoch);
    internal bool Faulted => Volatile.Read(ref _faulted) != 0;
    internal bool IsRunning => Volatile.Read(ref _running) != 0;
    internal InputPermission? Permission => Volatile.Read(ref _permission);
    internal void SetPermission(InputPermission? permission) => Volatile.Write(ref _permission, permission);

    internal bool TryRead(out CapturedInput input)
    {
        if (!_events.TryDequeue(out input))
            return false;
        Interlocked.Decrement(ref _queued);
        return true;
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
                return;
            if (_thread is not null)
                throw new InvalidOperationException("输入监听尚未停止。");

            using var ready = new ManualResetEventSlim();
            Exception? failure = null;
            SetPermission(null);
            Interlocked.Exchange(ref _faulted, 0);
            Volatile.Write(ref _stopRequested, 0);
            _thread = new Thread(() => Run(ready, error => failure = error)) { IsBackground = true, Name = "GestureFlow mouse messages" };
            _thread.Start();
            ready.Wait();
            if (failure is not null)
            {
                _thread.Join();
                _thread = null;
                throw new InvalidOperationException("无法启动 Windows 输入监听。", failure);
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            SetPermission(null);
            if (_thread is null)
                return;
            Volatile.Write(ref _stopRequested, 1);
            if (_thread.IsAlive)
                PostThreadMessage(_threadId, StopMessage, 0, 0);
            _thread.Join();
            _thread = null;
            while (TryRead(out _)) { }
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            Stop();
        }
    }

    private void Run(ManualResetEventSlim ready, Action<Exception> reportFailure)
    {
        nint hook = 0;
        nint foreground = 0;
        Mutex? installation = null;
        var ownsInstallation = false;
        nuint heartbeat = 0;
        try
        {
            installation = new Mutex(false, @"Local\GestureFlow.InputListener");
            try { ownsInstallation = installation.WaitOne(0); }
            catch (AbandonedMutexException) { ownsInstallation = true; }
            if (!ownsInstallation)
                throw new InvalidOperationException("输入监听已经由另一个实例启动。");
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, 0, 0, 0, 0);
            heartbeat = SetTimer(0, 0, 25, 0);
            if (heartbeat == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            foreground = SetWinEventHook(3, 3, 0, _windowEvent, 0, 0, 0);
            if (foreground == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            hook = SetWindowsHookEx(14, _mouseHook, GetModuleHandle(null), 0);
            if (hook == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            Volatile.Write(ref _running, 1);
            ready.Set();
            int result;
            long stopDeadline = 0;
            while ((result = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                if (Volatile.Read(ref _stopRequested) != 0)
                {
                    if (stopDeadline == 0)
                    {
                        _router.Cancel();
                        stopDeadline = Environment.TickCount64 + 250;
                    }
                    if (!_router.OwnsButton || Environment.TickCount64 >= stopDeadline)
                        break;
                }
                if (message.Id == 0x0113 && message.WParam == heartbeat && _router.OwnsButton)
                {
                    var tick = Environment.TickCount64;
                    _router.Advance(tick, CurrentPermission(tick));
                }
                else if (message.Id != StopMessage)
                    DispatchMessage(in message);
            }
            if (result < 0)
                Interlocked.Exchange(ref _faulted, 1);
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _faulted, 1);
            if (!IsRunning)
                reportFailure(error);
        }
        finally
        {
            SetPermission(null);
            if (heartbeat != 0) KillTimer(0, heartbeat);
            _router.Reset();
            if (hook != 0) UnhookWindowsHookEx(hook);
            if (foreground != 0) UnhookWinEvent(foreground);
            if (ownsInstallation) installation!.ReleaseMutex();
            installation?.Dispose();
            Volatile.Write(ref _running, 0);
            if (hook == 0)
                ready.Set();
        }
    }

    private void OnForegroundChanged(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        Interlocked.Increment(ref _focusEpoch);
        try { _router.Cancel(); }
        catch { Interlocked.Exchange(ref _faulted, 1); }
    }

    private nint OnMouse(int code, nuint message, nint data)
    {
        if (code == 0 && (Permission is not null || _router.OwnsButton))
        {
            try
            {
                var mouse = Marshal.PtrToStructure<MouseData>(data);
                if ((mouse.Flags & 3) == 0 && mouse.ExtraInfo != WindowsInputNative.InjectionMarker)
                {
                    var tick = Environment.TickCount64;
                    var packet = Decode((uint)message, mouse, tick);
                    var permission = CurrentPermission(tick);
                    var pointerMatches = permission is not null && packet.Signal == MouseSignal.Down
                        && MouseGestureRouter.MatchesTrigger(permission.Trigger, packet)
                        && (permission.IsDesktop || permission.Contains(packet.Position))
                        && WindowsInputNative.IsOverTarget(mouse.Point, permission);
                    if (_router.Route(packet, permission, pointerMatches))
                        return 1;
                }
            }
            catch
            {
                Interlocked.Exchange(ref _faulted, 1);
                SetPermission(null);
            }
        }
        return CallNextHookEx(0, code, message, data);
    }

    private InputPermission? CurrentPermission(long tick) =>
        !Faulted && Permission is { } grant && grant.ExpiresAt >= tick && grant.FocusEpoch == FocusEpoch
        && grant.Trigger is GestureTrigger.MouseX1 or GestureTrigger.MouseX2
        && WindowsInputNative.IsForeground(grant) ? grant : null;

    private static MousePacket Decode(uint message, MouseData data, long tick)
    {
        var side = (data.MouseDataValue >> 16) == 1 ? MouseButton.X1 : MouseButton.X2;
        var (signal, button) = message switch
        {
            0x0200 => (MouseSignal.Move, MouseButton.None),
            0x0204 => (MouseSignal.Down, MouseButton.Right),
            0x0205 => (MouseSignal.Up, MouseButton.Right),
            0x020B => (MouseSignal.Down, side),
            0x020C => (MouseSignal.Up, side),
            0x0201 => (MouseSignal.Down, MouseButton.Left),
            0x0202 => (MouseSignal.Up, MouseButton.Left),
            0x0207 => (MouseSignal.Down, MouseButton.Other),
            0x020A or 0x020E => (MouseSignal.Other, MouseButton.Other),
            _ => (MouseSignal.Up, MouseButton.Other)
        };
        var modifiers = button is MouseButton.Right or MouseButton.X1 or MouseButton.X2
            ? WindowsInputNative.HeldModifiers() : ShortcutModifiers.None;
        var otherHeld = signal == MouseSignal.Down && button is MouseButton.Right or MouseButton.X1 or MouseButton.X2
            && (WindowsInputNative.IsDown(0x01) || WindowsInputNative.IsDown(0x04)
                || (button != MouseButton.Right && WindowsInputNative.IsDown(0x02))
                || (button != MouseButton.X1 && WindowsInputNative.IsDown(0x05))
                || (button != MouseButton.X2 && WindowsInputNative.IsDown(0x06)));
        return new(signal, button, data.Point.ToGesturePoint(), tick, modifiers, OtherButtonHeld: otherHeld);
    }

    private void Enqueue(CapturedInput input)
    {
        if (Interlocked.Increment(ref _queued) > QueueLimit)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Exchange(ref _faulted, 1);
            SetPermission(null);
            return;
        }
        _events.Enqueue(input);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public WindowsInputNative.Point Point;
        public uint MouseDataValue, Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public WindowsInputNative.Point Point;
        public uint Private;
    }

    private delegate nint MouseHook(int code, nuint message, nint data);
    private delegate void WindowEvent(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int kind, MouseHook callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WindowEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(in Message msg);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, uint msg, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? module);
}
