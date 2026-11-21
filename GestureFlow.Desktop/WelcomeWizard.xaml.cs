using System.Windows;
using System.Windows.Input;
using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Profiles;
using GestureFlow.UI;
using PracticeButton = System.Windows.Input.MouseButton;

namespace GestureFlow.Desktop;

public partial class WelcomeWizard : Window
{
    private readonly WelcomeWizardViewModel _viewModel = new();
    private readonly GestureProfile _profile;
    private readonly GestureProfileSession _session;
    private readonly GestureTrigger _initialSideButton;
    private Point? _origin;
    private bool _skipped;

    internal WelcomeWizard(GestureTrigger sideButton, GestureProfile profile, double minimumDistance)
    {
        _initialSideButton = sideButton;
        _profile = profile;
        _session = new(profile, minimumDistance);
        InitializeComponent();
        TriggerChoice.SelectedIndex = sideButton == GestureTrigger.MouseX2 ? 1 : 0;
        PracticeWheel.CancelRadius = minimumDistance - GestureWheelMetrics.CrossingTolerance;
        PracticeWheel.Show(profile);
        DataContext = _viewModel;
    }

    internal bool WantsToEnable { get; private set; }
    internal GestureTrigger SelectedSideButton => _skipped ? _initialSideButton
        : TriggerChoice.SelectedIndex == 1 ? GestureTrigger.MouseX2 : GestureTrigger.MouseX1;

    private PracticeButton TriggerButton => SelectedSideButton == GestureTrigger.MouseX2
        ? PracticeButton.XButton2 : PracticeButton.XButton1;

    private void BackClick(object sender, RoutedEventArgs e) => Move(-1);
    private void NextClick(object sender, RoutedEventArgs e) => Move(1);
    private void FinishClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void SkipClick(object sender, RoutedEventArgs e)
    {
        _skipped = true;
        DialogResult = true;
    }

    private void EnableClick(object sender, RoutedEventArgs e)
    {
        WantsToEnable = true;
        DialogResult = true;
    }

    private void Move(int delta)
    {
        PracticeSurface.ReleaseMouseCapture();
        _viewModel.Move(delta);
    }

    private void PracticeDown(object sender, MouseButtonEventArgs e)
    {
        if (_origin is not null || e.ChangedButton != TriggerButton || !PracticeSurface.CaptureMouse())
            return;
        _origin = e.GetPosition(PracticeSurface);
        _session.Process(new(GestureInputKind.Press));
        PracticeWheel.Show(_profile);
        PracticeStatus.Text = "拖向目标方向，松开侧键确认。";
        e.Handled = true;
    }

    private void PracticeMove(object sender, MouseEventArgs e)
    {
        if (_origin is not { } origin)
            return;
        var delta = e.GetPosition(PracticeSurface) - origin;
        var (update, binding) = _session.Process(new(GestureInputKind.Move, new(delta.X, delta.Y)));
        PracticeWheel.Show(_profile, update.Candidate);
        PracticeStatus.Text = binding is { Enabled: true }
            ? $"松开侧键确认 · {binding.DisplayName}"
            : update.Candidate.HasValue ? "此方向未启用。" : "在中心松开可取消。";
    }

    private void PracticeUp(object sender, MouseButtonEventArgs e)
    {
        if (_origin is not { } origin || e.ChangedButton != TriggerButton)
            return;
        var delta = e.GetPosition(PracticeSurface) - origin;
        var (update, binding) = _session.Process(new(GestureInputKind.Release, new(delta.X, delta.Y)));
        _origin = null;
        PracticeSurface.ReleaseMouseCapture();
        PracticeWheel.Show(_profile, update.CommittedDirection);
        PracticeStatus.Text = binding is { Enabled: true }
            ? $"已确认 · {binding.DisplayName}"
            : update.CommittedDirection.HasValue ? "此方向未启用。" : "在中心松开，已取消。";
        e.Handled = true;
    }

    private void PracticeLostCapture(object sender, MouseEventArgs e)
    {
        if (_origin is null)
            return;
        _origin = null;
        _session.Process(new(GestureInputKind.Cancel));
        PracticeWheel.Show(_profile);
        PracticeStatus.Text = "练习已取消，可重新按住侧键。";
    }
}
