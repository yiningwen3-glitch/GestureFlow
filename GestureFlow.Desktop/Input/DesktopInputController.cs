using System.ComponentModel;
using System.Windows.Threading;
using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop.Applications;
using GestureFlow.Profiles;

namespace GestureFlow.Desktop.Input;

internal sealed class DesktopInputController(
    ForegroundApplicationService foreground,
    Func<RunningApplication, (string AppId, GestureProfile Profile)?> authorize,
    Func<GestureTrigger> sideButton,
    Action<RunningApplication?> observe,
    Action<string> report,
    Action stopped) : IDisposable
{
    private sealed record Capture(InputPermission Permission, ScreenGestureSession Session, long Deadline);
    private const int StepIntervalMilliseconds = 100;
    private const int KeyReleaseTimeoutMilliseconds = 750;

    private sealed record PendingAction(InputPermission Permission, KeyboardShortcut Shortcut, long NextStepAt, int Step = 0)
    {
        public KeyboardShortcut Current => Step == 0 ? Shortcut with { FollowingSteps = default } : Shortcut.FollowingSteps[Step - 1];
        public bool HasNextStep => Shortcut.IsSequence && Step < Shortcut.FollowingSteps.Length;
    }

    private readonly WindowsGestureInputSource _source = new();
    private readonly WindowsActionExecutor _executor = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private GestureWheelWindow? _wheel;
    private Capture? _capture;
    private PendingAction? _pending;
    private long _revision;
    private long _nextObservation;
    private bool _observing;
    private bool _paused;
    private bool _disposed;
    private nint _testWindow;
    private GestureProfile _testProfile = DefaultGestureProfiles.Create(GestureMode.Four);
    private double _minimumDistance = GestureWheelMetrics.DefaultTriggerDistance;

    public bool IsRunning => _source.IsRunning;
    public bool IsTestMode => _testWindow != 0;

    public void Start(nint testWindow = default)
    {
        if (IsRunning)
            return;

        _testWindow = testWindow;
        Invalidate();
        _source.Start();
        _timer.Tick += Tick;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _timer.Tick -= Tick;
        _testWindow = 0;
        Invalidate();
        _source.Stop();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Invalidate();
    }

    public void SetGestureSettings(GestureProfile profile, double minimumDistance)
    {
        _testProfile = profile;
        _minimumDistance = minimumDistance;
        Invalidate();
    }

    public void Invalidate()
    {
        _revision++;
        _nextObservation = 0;
        _source.SetPermission(null);
        _pending = null;
        DismissCapture();
    }

    private async void Tick(object? sender, EventArgs e)
    {
        try
        {
            if (!_source.IsRunning || _source.Faulted)
            {
                Stop();
                stopped();
                report("输入监听异常，已停止并取消待执行操作。");
                return;
            }

            var now = Environment.TickCount64;
            if (_capture is { } capture && (now > capture.Deadline || !Valid(capture.Permission)))
                DismissCapture();

            for (var count = 0; count < 128 && _source.TryRead(out var input); count++)
            {
                if (_source.Faulted)
                    return;
                Handle(input);
            }

            ProcessPending();
            if (_paused || _observing || now < _nextObservation)
                return;

            _nextObservation = now + 100;
            _observing = true;
            var revision = _revision;
            var epoch = _source.FocusEpoch;
            try
            {
                var running = await Task.Run(foreground.GetForegroundApplication);
                if (_disposed || !IsRunning || _paused || revision != _revision || epoch != _source.FocusEpoch)
                    return;

                _source.SetPermission(running is null ? null : CreatePermission(running, revision, epoch));
                observe(running);
            }
            finally { _observing = false; }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            if (!_disposed)
            {
                Stop();
                stopped();
                report($"输入监听已停止 · {error.Message}");
            }
        }
    }

    private InputPermission? CreatePermission(RunningApplication running, long revision, long epoch)
    {
        (string AppId, GestureProfile Profile)? grant = _testWindow != 0
            ? running.WindowHandle == _testWindow && running.WindowOwnerProcessId == Environment.ProcessId
                ? ("gestureflow-test", _testProfile) : null
            : running.WindowOwnerProcessId == Environment.ProcessId ? null : authorize(running);
        if (grant is null || !WindowsInputNative.GetWindowRect(running.WindowHandle, out var bounds))
            return null;

        var focus = WindowsInputNative.StableKeyboardFocus(running.WindowHandle);
        if (focus == 0 || !WindowsInputNative.CanSendToTarget((uint)running.WindowOwnerProcessId, focus))
            return null;
        var permission = new InputPermission(running.WindowHandle, focus, (uint)running.WindowOwnerProcessId, epoch, revision,
            Environment.TickCount64 + 650, grant.Value.AppId, sideButton(), grant.Value.Profile, bounds,
            running.ExecutablePath, running.InstallationStamp, running.WindowKind == ForegroundWindowKind.ExplorerDesktop);
        return WindowsInputNative.IsForeground(permission) ? permission : null;
    }

    private bool Valid(InputPermission permission) =>
        !_paused && _source.IsRunning && !_source.Faulted && permission.FocusEpoch == _source.FocusEpoch
        && _source.Permission is { } current && permission.SameTarget(current)
        && current.ExpiresAt >= Environment.TickCount64 && WindowsInputNative.IsForeground(permission)
        && WindowsInputNative.StableKeyboardFocus(permission.Window) == permission.KeyboardFocus;

    private void Handle(CapturedInput input)
    {
        if (input.Input.Kind == GestureInputKind.Cancel || !Valid(input.Permission))
        {
            _pending = null;
            DismissCapture();
            return;
        }
        if (input.Input.Kind == GestureInputKind.Press)
        {
            _pending = null;
            var placement = GestureWheelWindow.Locate(input.Input.Position);
            var startedCapture = new Capture(input.Permission,
                new(input.Permission.Profile, input.Input.Position, placement.Scale, _minimumDistance),
                Environment.TickCount64 + 5000);
            _capture = startedCapture;
            startedCapture.Session.Process(input.Input);
            _timer.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                if (!ReferenceEquals(_capture, startedCapture) || !Valid(startedCapture.Permission))
                    return;
                _wheel ??= new GestureWheelWindow();
                _wheel.Open(placement, startedCapture.Permission.Profile, _minimumDistance, startedCapture.Session.Candidate);
            }));
            return;
        }
        if (_capture is not { } capture || !capture.Permission.SameTarget(input.Permission))
            return;

        var (update, binding) = capture.Session.Process(input.Input);
        if (input.Input.Kind == GestureInputKind.Move)
        {
            _wheel?.Preview(capture.Permission.Profile, update.Candidate);
            return;
        }

        if (update.CommittedDirection is { } direction)
        {
            _capture = null;
            if (binding is { Enabled: true })
            {
                _pending = new(capture.Permission, binding.Shortcut, Environment.TickCount64);
                ProcessPending();
            }
            if (_wheel?.IsVisible == true)
                _wheel.Complete(capture.Permission.Profile, binding is { Enabled: true } ? direction : null);
        }
        else if (input.Input.Kind == GestureInputKind.Release)
            DismissCapture();
    }

    private void ProcessPending()
    {
        if (_pending is not { } action)
            return;
        var now = Environment.TickCount64;
        if (now - action.NextStepAt > KeyReleaseTimeoutMilliseconds)
        {
            _pending = null;
            report("等待物理按键释放超时，已取消快捷键。");
            return;
        }
        if (!Valid(action.Permission))
        {
            _pending = null;
            report("前台窗口或授权已变化，已取消快捷键。");
            return;
        }

        if (now < action.NextStepAt)
            return;

        var result = _executor.TryExecute(action.Current, () => CanCommit(action.Permission));
        if (result == ActionResult.ConflictingKeyboardState)
            return;
        if (result == ActionResult.Sent && action.HasNextStep)
        {
            _pending = action with { Step = action.Step + 1, NextStepAt = Environment.TickCount64 + StepIntervalMilliseconds };
            return;
        }

        _pending = null;
        report(result switch
        {
            ActionResult.Sent => $"已发送 {GestureFlow.UI.GestureLabels.Shortcut(action.Shortcut)}",
            ActionResult.ConflictingKeyboardState => "物理按键仍处于冲突状态，已取消快捷键。",
            ActionResult.NativeFailure => "Windows 拒绝或未完整接收快捷键，可能是权限限制。",
            ActionResult.UnsafeTarget => "前台窗口或授权已变化，已取消快捷键。",
            _ => "快捷键无效，已取消。"
        });
    }

    private bool CanCommit(InputPermission permission) => Valid(permission)
        && WindowsInputNative.CanSendToTarget(permission.ProcessId, permission.KeyboardFocus)
        && (_testWindow == permission.Window || (permission.InstallationStamp is { } expected
            && ExecutableInstallationStamp.Read(permission.ExecutablePath) == expected))
        && Valid(permission);

    private void DismissCapture()
    {
        _capture?.Session.Process(new(GestureInputKind.Cancel));
        _capture = null;
        if (_wheel?.IsVisible == true)
            _wheel.Hide();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _source.Dispose();
        _wheel?.Close();
    }
}
