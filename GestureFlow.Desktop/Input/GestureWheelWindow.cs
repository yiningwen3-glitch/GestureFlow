using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GestureFlow.Core;
using GestureFlow.Profiles;
using GestureFlow.UI;

namespace GestureFlow.Desktop.Input;

internal sealed class GestureWheelWindow : Window
{
    private const int WheelSize = WheelPlacement.WheelSize;
    private readonly RadialWheel _wheel = new();
    private nint _handle;
    private HwndSource? _source;
    private int _presentation;

    public GestureWheelWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Background = Brushes.Transparent;
        Width = Height = WheelSize;

        Content = _wheel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        var style = WindowsInputNative.GetWindowStyle(_handle, -20);
        WindowsInputNative.SetWindowStyle(_handle, -20, style | 0x08000000 | 0x80 | 0x20);
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(HitTest);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            _presentation++;
            _source?.RemoveHook(HitTest);
        }
    }

    internal static WheelPlacement Locate(GesturePoint origin)
    {
        var point = new WindowsInputNative.Point((int)origin.X, (int)origin.Y);
        var monitor = WindowsInputNative.MonitorFromPoint(point, 2);
        var dpi = monitor != 0 && WindowsInputNative.GetDpiForMonitor(monitor, 0, out var detected, out _) == 0 ? detected : 96u;
        var info = new WindowsInputNative.MonitorInfo { Size = (uint)Marshal.SizeOf<WindowsInputNative.MonitorInfo>() };
        return WheelPlacement.Around(point, dpi,
            monitor != 0 && WindowsInputNative.GetMonitorInfo(monitor, ref info) ? info.Work : null);
    }

    public void Open(WheelPlacement placement, GestureProfile profile, double minimumDistance, GestureDirection? candidate = null)
    {
        _presentation++;
        _wheel.IsDark = (Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as SolidColorBrush)?.Color.R < 128;
        _wheel.BeginAnimation(OpacityProperty, null);
        _wheel.CancelRadius = minimumDistance - GestureWheelMetrics.CrossingTolerance;
        Preview(profile, candidate);
        new WindowInteropHelper(this).EnsureHandle();
        // Native positioning accepts physical coordinates, including monitors left of the primary display.
        WindowsInputNative.SetWindowPos(_handle, -1, placement.X, placement.Y, placement.Size, placement.Size, 0x10);
        Show();
        WindowsInputNative.SetWindowPos(_handle, -1, placement.X, placement.Y, placement.Size, placement.Size, 0x10);
        Motion.Reveal(_wheel, "WheelMotion");
    }

    public void Preview(GestureProfile profile, GestureDirection? direction) => _wheel.Show(profile, direction);

    public void Complete(GestureProfile profile, GestureDirection? direction)
    {
        _wheel.Show(profile, direction);
        if (!SystemParameters.ClientAreaAnimation)
        {
            Hide();
            return;
        }

        var fade = new DoubleAnimation(_wheel.Opacity, 0, TimeSpan.FromMilliseconds(90))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var presentation = _presentation;
        fade.Completed += (_, _) =>
        {
            if (_presentation == presentation)
                Hide();
        };
        _wheel.BeginAnimation(OpacityProperty, fade);
    }

    private static nint HitTest(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0084)
        {
            handled = true;
            return -1;
        }
        return 0;
    }
}
