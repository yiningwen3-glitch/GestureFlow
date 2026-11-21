using System.Windows;

namespace GestureFlow.Desktop;

public partial class CloseBehaviorDialog : Window
{
    public CloseBehaviorDialog() => InitializeComponent();

    internal WindowCloseAction Choice { get; private set; }

    private void HideClick(object sender, RoutedEventArgs e)
    {
        Choice = WindowCloseAction.Hide;
        DialogResult = true;
    }

    private void ExitClick(object sender, RoutedEventArgs e)
    {
        Choice = WindowCloseAction.Exit;
        DialogResult = true;
    }
}
