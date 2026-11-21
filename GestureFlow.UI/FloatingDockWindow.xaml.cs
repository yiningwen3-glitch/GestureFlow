using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GestureFlow.UI;

public partial class FloatingDockWindow : Window
{
    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x80;
    private const uint MoveWithoutActivation = 0x0015; // NOSIZE | NOZORDER | NOACTIVATE
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _dragTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private HwndSource? _source;
    private DockPosition? _anchor;
    private DockPosition? _savedPosition;
    private DragStart? _drag;
    private bool _autoCollapse;
    private bool _closed;
    private bool _placementQueued;
    private sealed record DragStart(Point Cursor, Rect Window);

    public FloatingDockWindow()
    {
        InitializeComponent();
        _collapseTimer.Tick += CollapseAfterIdle;
        _dragTimer.Tick += TrackDrag;
        SizeChanged += (_, _) => QueuePlacement();
        DpiChanged += (_, _) => QueuePlacement();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                QueuePlacement();
                ScheduleCollapse();
            }
            else
            {
                _collapseTimer.Stop();
                CancelDrag();
            }
        };
    }

    public event Action<DockProfileOption>? ProfileRequested;
    public event Action? PauseRequested;
    public event Action? SettingsRequested;
    public event Action? HideRequested;
    public event Action? InteractionCompleted;
    public event Action<DockPosition>? PositionChanged;

    public bool IsExpanded => ExpandedPanel.Visibility == Visibility.Visible;

    public void ApplyPreferences(FloatingDockPreferences preferences, bool resetExpansion)
    {
        Topmost = preferences.AlwaysOnTop;
        Opacity = preferences.Opacity;
        _autoCollapse = preferences.AutoCollapse;
        if (_savedPosition != preferences.Position)
        {
            _savedPosition = preferences.Position;
            _anchor = _savedPosition;
        }
        if (!_autoCollapse)
            _collapseTimer.Stop();
        if (resetExpansion)
            SetExpanded(!preferences.StartCollapsed);
        else
            ScheduleCollapse();
        UpdatePlacement();
    }

    public void UpdateState(FloatingDockState state)
    {
        if (DataContext is FloatingDockState previous)
        {
            if (previous.Application != state.Application
                || previous.Options.FirstOrDefault()?.AppId != state.Options.FirstOrDefault()?.AppId)
                ShowFeedback(null);
            if (previous == (state with { Options = previous.Options }) && previous.Options.SequenceEqual(state.Options))
                return;
        }
        DataContext = state;
    }

    public void ShowFeedback(string? message) => Feedback.Text = message ?? "";

    public void UpdatePlacement()
    {
        if (_source is null || !IsLoaded || !IsVisible || _drag is not null
            || !Native.GetWindowRect(_source.Handle, out var window) || window.Bottom <= window.Top)
            return;

        Point point;
        if (_anchor is { } anchor)
            point = new() { X = anchor.X, Y = anchor.Y };
        else if (!Native.GetCursorPos(out point))
            point = default;
        var monitor = Native.MonitorFromPoint(point, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, ref info))
            return;
        var fit = new DockWorkArea(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom)
            .Fit(_anchor, window.Right - window.Left, window.Bottom - window.Top);
        _anchor ??= fit;
        if (window.Left != fit.X || window.Top != fit.Y)
            Native.SetWindowPos(_source.Handle, 0, fit.X, fit.Y, 0, 0, MoveWithoutActivation);
    }

    public void ResetPosition()
    {
        _anchor = _savedPosition = null;
        UpdatePlacement();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        var style = Native.GetWindowLongPtr(handle, -20);
        Native.SetWindowLongPtr(handle, -20, style | NoActivate | ToolWindow);
        _source?.AddHook(WindowMessage);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _collapseTimer.Stop();
        CancelDrag();
        _source?.RemoveHook(WindowMessage);
        _source = null;
        base.OnClosed(e);
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        if (message is 0x007E or 0x001A) // display configuration or work area changed
            QueuePlacement();
        return 0;
    }

    private void QueuePlacement()
    {
        if (_closed || _placementQueued)
            return;
        _placementQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _placementQueued = false;
            if (!_closed)
                UpdatePlacement();
        }));
    }

    public void Collapse() => SetExpanded(false);

    private void SetExpanded(bool expanded)
    {
        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        DockCard.CornerRadius = new(expanded ? 18 : 28);
        ExpandGlyph.Text = expanded ? "\uE70E" : "\uE70D";
        _collapseTimer.Stop();
        ScheduleCollapse();
    }

    private void ScheduleCollapse()
    {
        if (_autoCollapse && IsVisible && IsExpanded && _drag is null && !IsMouseOver)
            _collapseTimer.Start();
    }

    private void ToggleExpanded(object sender, RoutedEventArgs e)
    {
        SetExpanded(!IsExpanded);
        InteractionCompleted?.Invoke();
    }

    private void DockMouseEnter(object sender, MouseEventArgs e) => _collapseTimer.Stop();

    private void DockMouseLeave(object sender, MouseEventArgs e) => ScheduleCollapse();

    private void CollapseAfterIdle(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (!_autoCollapse || !IsVisible || _drag is not null || IsMouseOver)
            return;
        SetExpanded(false);
        InteractionCompleted?.Invoke();
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (_source is null || !Native.GetCursorPos(out var cursor) || !Native.GetWindowRect(_source.Handle, out var window))
            return;
        e.Handled = true;
        _drag = new(cursor, window);
        _collapseTimer.Stop();
        _dragTimer.Start();
    }

    private void TrackDrag(object? sender, EventArgs e)
    {
        if ((Native.GetAsyncKeyState(1) & 0x8000) == 0)
            FinishDrag();
        else
            MoveWithPointer();
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (_drag is null)
            return;
        e.Handled = true;
        FinishDrag();
    }

    private void MoveWithPointer()
    {
        if (_source is null || _drag is not { } drag || !Native.GetCursorPos(out var cursor))
            return;
        Native.SetWindowPos(_source.Handle, 0, drag.Window.Left + cursor.X - drag.Cursor.X,
            drag.Window.Top + cursor.Y - drag.Cursor.Y, 0, 0, MoveWithoutActivation);
    }

    private void FinishDrag()
    {
        if (_drag is not { } drag)
            return;
        MoveWithPointer();
        CancelDrag();
        if (_source is not null && Native.GetWindowRect(_source.Handle, out var moved)
            && (moved.Left != drag.Window.Left || moved.Top != drag.Window.Top))
        {
            _anchor = new(moved.Left, moved.Top);
            UpdatePlacement();
            if (Native.GetWindowRect(_source.Handle, out moved))
            {
                _anchor = new(moved.Left, moved.Top);
                PositionChanged?.Invoke(_anchor);
            }
        }
        ScheduleCollapse();
        InteractionCompleted?.Invoke();
    }

    private void CancelDrag()
    {
        _drag = null;
        _dragTimer.Stop();
    }

    private void ChooseProfile(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DockProfileOption option })
            ProfileRequested?.Invoke(option);
        InteractionCompleted?.Invoke();
    }

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        PauseRequested?.Invoke();
        InteractionCompleted?.Invoke();
    }

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
        InteractionCompleted?.Invoke();
    }

    private void HideClick(object sender, RoutedEventArgs e)
    {
        HideRequested?.Invoke();
        InteractionCompleted?.Invoke();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }

    private static class Native
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern nint SetWindowLongPtr(nint window, int index, nint value);
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll")]
        public static extern nint MonitorFromPoint(Point point, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    }
}
