using System.Windows.Media.Animation;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GestureFlow.Core;
using GestureFlow.Lab.Simulation;
using GestureFlow.Profiles;
using GestureFlow.UI;

namespace GestureFlow.Lab;

public partial class MainWindow : Window
{
    private const int TraceLimit = 80;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LabProfileEditor _editor = new(DefaultGestureProfiles.Create(GestureMode.Four));
    private readonly HashSet<GestureDirection> _invalidNames = [];
    private GestureLabSession _session;
    private CancellationTokenSource? _playback;
    private List<TimedGestureInput>? _recording;
    private long _recordingStart;
    private GestureTestCase? _testCase;
    private Button? _shortcutButton;
    private ShortcutChordRecording? _shortcutChord;
    private bool _refreshingSettings;
    private bool _ready;
    private bool _closed;

    public MainWindow() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GestureFlow")) { }

    public MainWindow(string profileDirectory)
    {
        _profileDirectory = profileDirectory;
        _session = new(_editor.Profile);
        InitializeComponent();
        IGestureInputSource inputSource = Surface;
        inputSource.InputReceived += OnSurfaceInput;
        _ready = true;
        RefreshSettings();
        RefreshControls();
        InitializeVirtualApplications();
        InitializeDockLab();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _labDock?.Close();
        _playback?.Cancel();
        base.OnClosed(e);
    }

    private void OnSurfaceInput(object? sender, GestureInputEvent input) =>
        Process(new(_clock.ElapsedMilliseconds, input.Kind, input.Position.X, input.Position.Y));

    private void Process(TimedGestureInput input)
    {
        if (_recording is { } recording)
        {
            if (recording.Count == 0)
                _recordingStart = input.Milliseconds;
            recording.Add(input with { Milliseconds = input.Milliseconds - _recordingStart });
            CaseStatus.Text = $"录制中 · {recording.Count} 条事件";
        }

        var trace = _session.Process(input);
        var direction = trace.Update.CommittedDirection ?? trace.Update.Candidate;
        var tracking = trace.Update.State == GestureState.Tracking;
        var outcome = trace.Outcome switch
        {
            GestureOutcome.Selected => "已模拟执行",
            GestureOutcome.Click => "侧键轻点 · 已取消",
            GestureOutcome.LongPress => "长按 · 无手势",
            GestureOutcome.DeadZone => "在中心松开 · 已取消",
            GestureOutcome.Cancelled => "已取消手势",
            GestureOutcome.Disabled => "该方向已禁用",
            _ when tracking && trace.MatchedBinding is { Enabled: true } binding => $"{binding.DisplayName} · 松开执行",
            _ when tracking && direction.HasValue => "该方向无可用命令",
            _ when tracking => "中心安全区 · 松开取消",
            _ => "等待虚拟输入"
        };

        if (input.Kind == GestureInputKind.Press && tracking)
        {
            Wheel.BeginAnimation(OpacityProperty, null);
            Wheel.Opacity = 1;
            Wheel.Show(_session.Profile, anchor: Surface.DragAnchor);
            Motion.Reveal(Wheel, "WheelMotion");
        }
        else if (tracking && input.Kind == GestureInputKind.Move)
            Wheel.Show(_session.Profile, direction, Surface.DragAnchor);
        else if (trace.Update.CommittedDirection.HasValue)
        {
            Wheel.Show(_session.Profile, trace.Preview?.Direction, Surface.DragAnchor);
            var previousOpacity = Wheel.Opacity;
            Wheel.Opacity = 0;
            if (SystemParameters.ClientAreaAnimation)
                Wheel.BeginAnimation(OpacityProperty, new DoubleAnimation(previousOpacity, 0, TimeSpan.FromMilliseconds(90))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
        }
        else if (trace.Outcome is GestureOutcome.Click or GestureOutcome.LongPress or GestureOutcome.DeadZone or GestureOutcome.Cancelled)
        {
            Wheel.BeginAnimation(OpacityProperty, null);
            Wheel.Opacity = 0;
        }
        if (trace.Preview is { } preview)
        {
            SelectionText.Text = $"已选择：{preview.DisplayName}";
            ShortcutText.Text = $"模拟快捷键：{GestureLabels.Shortcut(preview.Shortcut)}";
        }
        else if (input.Kind == GestureInputKind.Press || trace.Outcome != GestureOutcome.None)
        {
            SelectionText.Text = "已选择：—";
            ShortcutText.Text = "模拟快捷键：—";
        }
        if (trace.Outcome != GestureOutcome.None || tracking)
            OutcomeText.Text = outcome;

        InputText.Text = $"虚拟事件：{input.Kind} · {input.Milliseconds} ms · ({input.X:0.0}, {input.Y:0.0})";
        DirectionText.Text = $"识别方向：{(direction is { } value ? GestureLabels.Direction(value) : "—")}";
        DistanceText.Text = $"移动距离：{trace.Distance:0.00} DIP";
        StateText.Text = $"状态机：{trace.Update.State}";
        MatchText.Text = $"匹配命令：{trace.MatchedBinding?.DisplayName ?? "—"}";
        DebugResultText.Text = $"执行结果：{(trace.Preview is { } result ? GestureLabels.Shortcut(result.Shortcut) : outcome)}";
        var detail = trace.Preview is { } selected
            ? $"{selected.DisplayName} · {GestureLabels.Shortcut(selected.Shortcut)}"
            : outcome;
        var line = $"{input.Milliseconds,7} ms  {input.Kind,-7} {trace.Update.State,-8}  {detail}";
        if (TraceLog.Items.Count == TraceLimit)
            TraceLog.Items.RemoveAt(0);
        TraceLog.Items.Add(line);
        if (TraceLog.IsVisible)
            TraceLog.ScrollIntoView(line);
    }

    private async void DirectionClick(object sender, RoutedEventArgs e)
    {
        var direction = Enum.Parse<GestureDirection>((string)((Button)sender).Tag);
        await PlayAsync(GestureScenarios.Direction(direction));
    }

    private async void ScenarioClick(object sender, RoutedEventArgs e)
    {
        var scenario = Enum.Parse<GestureScenario>((string)((Button)sender).Tag);
        await PlayAsync(GestureScenarios.Create(scenario));
    }

    private async void ReplayClick(object sender, RoutedEventArgs e)
    {
        if (_testCase is { } testCase && _recording is null)
            await PlayAsync(testCase.Events, testCase.Profile);
    }

    private async Task PlayAsync(IReadOnlyList<TimedGestureInput> inputs, GestureProfile? replayProfile = null)
    {
        if (_playback is not null)
            return;

        Surface.CancelDrag();
        using var playback = new CancellationTokenSource();
        _playback = playback;
        _session = new(replayProfile ?? _editor.Profile);
        ModeLabel.Text = FormatMode(_session.Profile.Mode);
        TraceLog.Items.Clear();
        RefreshControls();

        var start = replayProfile is null ? _clock.ElapsedMilliseconds : 0;
        var last = new TimedGestureInput(start, GestureInputKind.Cancel);
        var timer = Stopwatch.StartNew();
        try
        {
            foreach (var input in inputs)
            {
                var wait = input.Milliseconds - timer.ElapsedMilliseconds;
                while (wait > 0)
                {
                    await Task.Delay((int)Math.Min(wait, int.MaxValue), playback.Token);
                    wait = input.Milliseconds - timer.ElapsedMilliseconds;
                }

                playback.Token.ThrowIfCancellationRequested();
                last = input with { Milliseconds = start + input.Milliseconds };
                Process(last);
            }

            if (replayProfile is not null)
                CaseStatus.Text = $"回放完成 · {inputs.Count} 条事件";
        }
        catch (OperationCanceledException) when (playback.IsCancellationRequested)
        {
            if (!_closed)
            {
                if (_session.State == GestureState.Tracking)
                    Process(last with
                    {
                        Milliseconds = Math.Max(last.Milliseconds, start + timer.ElapsedMilliseconds),
                        Kind = GestureInputKind.Cancel
                    });
                if (_recording is null)
                    CaseStatus.Text = "已停止播放";
            }
        }
        finally
        {
            _playback = null;
            if (!_closed)
            {
                if (replayProfile is not null)
                    RefreshProfile();
                RefreshControls();
            }
        }
    }

    private void StopPlaybackClick(object sender, RoutedEventArgs e) => _playback?.Cancel();

    private void RecordClick(object sender, RoutedEventArgs e)
    {
        Surface.CancelDrag();
        if (_recording is null)
        {
            _recording = [];
            CaseStatus.Text = "录制中 · 等待画布拖动或预置场景";
        }
        else
        {
            if (_recording.Count > 0)
            {
                _testCase = new GestureTestCase(_editor.Profile, _recording.AsReadOnly());
                CaseStatus.Text = $"录制完成 · {_recording.Count} 条事件";
            }
            else
                CaseStatus.Text = "本次录制没有事件";
            _recording = null;
        }
        RefreshControls();
    }

    private void RefreshControls()
    {
        var idle = _playback is null;
        StopPlayback.IsEnabled = !idle;
        Tabs.IsEnabled = Surface.IsEnabled = idle;
        SettingsPanel.IsEnabled = _recording is null;
        SaveProfileButton.IsEnabled = _invalidNames.Count == 0;
        RecordButton.Content = _recording is null ? "开始录制" : "停止录制";
        LoadCaseButton.IsEnabled = _recording is null;
        SaveCaseButton.IsEnabled = ReplayButton.IsEnabled = _recording is null && _testCase is not null;
    }

    private void RefreshProfile()
    {
        _session = new(_editor.Profile);
        ModeLabel.Text = FormatMode(_editor.Profile.Mode);
        Wheel.BeginAnimation(OpacityProperty, null);
        Wheel.Opacity = 1;
        Wheel.Show(_editor.Profile);
    }

    private static string FormatMode(GestureMode mode) => $"{(int)mode} 方向轮盘";

    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;

        var dark = ThemePicker.SelectedIndex == 1;
#pragma warning disable WPF0001 // The WPF Fluent theme API is experimental in .NET 10.
        Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        Wheel.IsDark = dark;
    }
}
