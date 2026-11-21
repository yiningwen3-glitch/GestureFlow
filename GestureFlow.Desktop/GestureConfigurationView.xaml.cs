using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using GestureFlow.Core;
using GestureFlow.Profiles;
using GestureFlow.UI;

namespace GestureFlow.Desktop;

public partial class GestureConfigurationView : UserControl
{
    private GestureBindingDraft? _recording;
    private Button? _recordButton;
    private ShortcutChordRecording? _chord;
    private GestureDirection? _selectedDirection;

    internal GestureConfigurationViewModel Model { get; } = new(DefaultGestureProfiles.Create(GestureMode.Four));

    public GestureConfigurationView()
    {
        InitializeComponent();
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is null or nameof(GestureConfigurationViewModel.Profile))
                UpdatePreview();
        };
        DataContext = Model;
        UpdatePreview();
    }

    public event RoutedEventHandler? SaveRequested;
    internal void SetEditing(bool value) => EditorCard.IsEnabled = value;

    internal void BeginEditing() => ModePicker.Focus();

    internal void SetProfile(GestureProfile profile)
    {
        StopRecording();
        _selectedDirection = null;
        Model.Load(profile);
    }

    internal void SetSensitivity(WheelSensitivity sensitivity) => PreviewWheel.CancelRadius = sensitivity.CenterRadius();

    private void UpdatePreview() => PreviewWheel.Show(Model.Profile, _recording?.Direction ?? _selectedDirection);

    private void LayoutChanged(object sender, SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width < 690;
        Grid.SetColumnSpan(WheelPanel, stacked ? 3 : 1);
        Grid.SetColumn(EditorCard, stacked ? 0 : 2);
        Grid.SetColumnSpan(EditorCard, stacked ? 3 : 1);
        Grid.SetRow(EditorCard, stacked ? 1 : 0);
        PreviewWheel.Height = stacked ? 216 : 292;
    }

    private void DirectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GestureBindingDraft row })
            FocusDirection(row);
    }

    private void WheelDirectionClick(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(PreviewWheel);
        var offset = new GesturePoint(point.X - PreviewWheel.ActualWidth / 2, point.Y - PreviewWheel.ActualHeight / 2);
        var radius = PreviewWheel.RenderedRadius;
        if (Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y) <= radius
            && DirectionRecognizer.Recognize(default, offset, Model.Mode, PreviewWheel.RenderedCenterRadius) is { } direction
            && Model.Rows.FirstOrDefault(row => row.Direction == direction) is { } row)
            FocusDirection(row);
    }

    private void FocusDirection(GestureBindingDraft row)
    {
        if (!EditorCard.IsEnabled || BindingRows.ItemContainerGenerator.ContainerFromItem(row) is not ContentPresenter item)
            return;
        StopRecording();
        _selectedDirection = row.Direction;
        UpdatePreview();
        var box = item.ContentTemplate.FindName("ActionNameBox", item) as TextBox;
        if (box is not null) Motion.Reveal(box, "SelectionMotion");
        box?.BringIntoView();
        box?.Focus();
        box?.SelectAll();
    }

    private void TypeShortcutClick(object sender, RoutedEventArgs e)
    {
        StopRecording();
        if (sender is Button { DataContext: GestureBindingDraft row }
            && ShortcutTextDialog.Edit(Window.GetWindow(this), row.ShortcutLabel) is { } value
            && Model.SetShortcut(row, value))
            Model.Feedback = $"已设置 {GestureLabels.Shortcut(value)}。";
    }

    private void RecordClick(object sender, RoutedEventArgs e)
    {
        StopRecording();
        if (sender is not Button { DataContext: GestureBindingDraft row } button)
            return;

        button.Focus();
        _recordButton = button;
        _recording = row;
        _chord = new();
        row.SetRecording(true);
        Model.Feedback = "按住组合键，松开所有主键后确认。只按 Esc 可取消。";
        UpdatePreview();
    }

    private void RecordLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _recordButton))
            StopRecording();
    }

    private void ViewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
            StopRecording();
    }

    private void CaptureKey(object sender, KeyEventArgs e)
    {
        if (_chord is not { } chord)
            return;

        e.Handled = true;
        var key = ShortcutRecorder.GetKey(e);
        var modifiers = Keyboard.Modifiers;
        if (e.IsRepeat || ShortcutRecorder.IsModifier(key))
            return;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopRecording();
            Model.Feedback = "已取消快捷键录制。";
            return;
        }

        Model.Feedback = chord.Press(key, modifiers)
            ? $"正在录制 {GestureLabels.Shortcut(chord.Preview!)}。松开主键后确认。"
            : "此按键无法录制。请尝试另一个主键，或按 Esc 取消。";
    }

    private void ReleaseKey(object sender, KeyEventArgs e)
    {
        if (_recording is not { } row || _chord is not { } chord)
            return;

        e.Handled = true;
        if (chord.Release(ShortcutRecorder.GetKey(e)) is not { } shortcut || !Model.SetShortcut(row, shortcut))
            return;

        StopRecording();
        Model.Feedback = $"已录制 {GestureLabels.Shortcut(shortcut)}。";
    }

    private void StopRecording()
    {
        if (_recording is not { } row)
            return;

        _recording = null;
        _recordButton = null;
        _chord = null;
        row.SetRecording(false);
        Model.Feedback = null;
        UpdatePreview();
    }

    private void RestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        StopRecording();
        Model.RestoreDefaults();
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        StopRecording();
        SaveRequested?.Invoke(this, e);
    }

}

public sealed class WheelDarkThemeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SolidColorBrush { Color.R: < 128 };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
