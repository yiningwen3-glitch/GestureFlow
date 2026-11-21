using System.Windows;
using System.Windows.Controls;
using GestureFlow.Core.Protection;
using GestureFlow.UI;

namespace GestureFlow.Desktop.Input;

internal sealed class InputTestWindow : Window
{
    private readonly TextBlock _status = new() { Text = "测试监听关闭。普通右键应打开编辑框菜单。", TextWrapping = TextWrapping.Wrap };
    private readonly Button _toggle = new() { Content = "仅为此窗口启用", Margin = new(0, 0, 8, 0), Padding = new(9, 5, 9, 5) };
    private readonly TextBlock _trigger = new() { Margin = new(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    private bool _running;

    public InputTestWindow(Action start, Action stop, Action pause)
    {
        Title = "GestureFlow 输入测试";
        Width = 670;
        Height = 410;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _toggle.Click += (_, _) =>
        {
            if (_running)
                stop();
            else
                start();
        };
        var pauseButton = new Button { Content = "暂停 / 恢复", Padding = new(9, 5, 9, 5) };
        pauseButton.Click += (_, _) => pause();
        var actions = new WrapPanel { Margin = new(0, 0, 0, 12) };
        actions.Children.Add(_toggle);
        actions.Children.Add(pauseButton);
        var editor = new TextBox
        {
            Text = "按住所选侧键，拖向目标方向，再松开以测试快捷键。也可用普通右键打开此编辑框的菜单。",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new(10), Margin = new(0, 12, 0, 0)
        };
        editor.PreviewKeyDown += (_, _) => _status.Text = "测试编辑框已收到键盘事件。";
        var layout = new DockPanel { Margin = new(20) };
        DockPanel.SetDock(actions, Dock.Top);
        DockPanel.SetDock(_trigger, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        layout.Children.Add(actions);
        layout.Children.Add(_trigger);
        layout.Children.Add(_status);
        layout.Children.Add(editor);
        Content = layout;
    }

    public void SetTrigger(GestureTrigger trigger) =>
        _trigger.Text = $"{ProtectionLabels.Trigger(trigger)} · 在设置切换侧键";

    public void SetRunning(bool running)
    {
        _running = running;
        _toggle.Content = running ? "停止测试监听" : "仅为此窗口启用";
        if (!running)
            Report("测试监听关闭。");
        else
            Report("测试监听已开启，仅当前测试窗口可触发。");
    }

    public void Report(string message) => _status.Text = message;
}
