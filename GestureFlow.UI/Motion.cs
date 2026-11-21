using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace GestureFlow.UI;

public static class Motion
{
    public static readonly DependencyProperty EntranceProperty = DependencyProperty.RegisterAttached(
        "Entrance", typeof(string), typeof(Motion), new PropertyMetadata(null, EntranceChanged));

    public static string? GetEntrance(DependencyObject element) => (string?)element.GetValue(EntranceProperty);
    public static void SetEntrance(DependencyObject element, string? value) => element.SetValue(EntranceProperty, value);

    private static void EntranceChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        element.IsVisibleChanged -= VisibilityChanged;
        if (e.NewValue is string) element.IsVisibleChanged += VisibilityChanged;
    }

    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (e.NewValue is true && GetEntrance(element) is { } key)
            Reveal(element, key);
    }

    public static void Reveal(FrameworkElement element, string durationKey)
    {
        Fade(element, durationKey, 0.68);
    }

    public static readonly DependencyProperty ButtonFeedbackProperty = DependencyProperty.RegisterAttached(
        "ButtonFeedback", typeof(bool), typeof(Motion), new PropertyMetadata(false, ButtonFeedbackChanged));

    public static bool GetButtonFeedback(DependencyObject element) => (bool)element.GetValue(ButtonFeedbackProperty);
    public static void SetButtonFeedback(DependencyObject element, bool value) => element.SetValue(ButtonFeedbackProperty, value);

    private static void ButtonFeedbackChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ButtonBase button) return;
        if (e.NewValue is true)
        {
            button.MouseEnter += ButtonStateChanged;
            button.PreviewMouseLeftButtonDown += Pressed;
        }
        else
        {
            button.MouseEnter -= ButtonStateChanged;
            button.PreviewMouseLeftButtonDown -= Pressed;
        }
    }

    private static void Pressed(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        Fade((FrameworkElement)sender, "PressMotion", 0.85);

    private static void ButtonStateChanged(object sender, System.Windows.Input.MouseEventArgs e) =>
        Fade((FrameworkElement)sender, "HoverMotion", 0.94);

    private static void Fade(FrameworkElement element, string key, double from)
    {
        if (!SystemParameters.ClientAreaAnimation || element.TryFindResource(key) is not Duration duration) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, 1, duration)
        {
            EasingFunction = element.TryFindResource("EaseOut") as IEasingFunction, FillBehavior = FillBehavior.Stop
        });
    }
}
