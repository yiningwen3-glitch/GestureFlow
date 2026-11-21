using System.Windows;
using System.Windows.Controls;
using GestureFlow.Core;

namespace GestureFlow.UI;

public static class ShortcutTextDialog
{
    public static KeyboardShortcut? Edit(Window owner, string current)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        var window = new Window
        {
            Title = "输入快捷键", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height, Width = 370, ResizeMode = ResizeMode.NoResize, Content = panel
        };
        var input = new TextBox { Text = current, Margin = new Thickness(0, 8, 0, 8) };
        var steps = new StackPanel();
        var stepList = new ScrollViewer { Content = steps, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var add = new Button { Content = "添加一步", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 8) };
        var fields = new List<TextBox>();
        var mode = new ComboBox();
        System.Windows.Automation.AutomationProperties.SetName(mode, "快捷键输入方式");
        mode.Items.Add("同时按");
        mode.Items.Add("依次按");
        mode.SelectedIndex = ShortcutRecorder.ParseSequence(current) is null ? 0 : 1;
        var label = new Label { Target = input };
        var preview = new TextBlock();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var accept = new Button { Content = "确定", IsDefault = true, MinWidth = 68, Margin = new Thickness(0, 8, 6, 0) };
        buttons.Children.Add(accept);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, MinWidth = 68, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(mode);
        panel.Children.Add(label);
        panel.Children.Add(input);
        panel.Children.Add(stepList);
        panel.Children.Add(add);
        panel.Children.Add(preview);
        panel.Children.Add(buttons);
        KeyboardShortcut? value = null;
        void Validate()
        {
            var sequential = mode.SelectedIndex == 1;
            input.Visibility = sequential ? Visibility.Collapsed : Visibility.Visible;
            stepList.Visibility = add.Visibility = sequential ? Visibility.Visible : Visibility.Collapsed;
            add.IsEnabled = fields.Count < 16;
            label.Content = sequential ? "每行填写一步，例如 Ctrl+K 或 Enter" : "用 + 连接按键，例如 Ctrl+Shift+Esc";
            label.Target = sequential ? fields.FirstOrDefault() : input;
            value = sequential
                ? fields.All(field => ShortcutRecorder.Parse(field.Text) is not null)
                    ? ShortcutRecorder.ParseSequence(string.Join(", ", fields.Select(field => field.Text))) : null
                : ShortcutRecorder.Parse(input.Text);
            accept.IsEnabled = value is not null;
            preview.Text = value is null
                ? (sequential ? "请输入 2 至 16 步，每步至少包含一个主键。" : "请输入有效组合，至少包含一个主键。")
                : GestureLabels.Shortcut(value);
        }
        void RefreshSteps()
        {
            steps.Children.Clear();
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                var position = index;
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                DockPanel.SetDock(actions, Dock.Right);
                row.Children.Add(actions);
                void Action(string content, string name, bool enabled, Action click)
                {
                    var button = new Button { Content = content, IsEnabled = enabled, Margin = new Thickness(4, 0, 0, 0) };
                    System.Windows.Automation.AutomationProperties.SetName(button, $"第 {position + 1} 步：{name}");
                    button.Click += (_, _) => { click(); RefreshSteps(); };
                    actions.Children.Add(button);
                }
                Action("↑", "上移", index > 0, () => (fields[position], fields[position - 1]) = (fields[position - 1], field));
                Action("↓", "下移", index + 1 < fields.Count, () => (fields[position], fields[position + 1]) = (fields[position + 1], field));
                Action("删除", "删除", fields.Count > 2, () => fields.Remove(field));
                System.Windows.Automation.AutomationProperties.SetName(field, $"第 {index + 1} 步快捷键");
                if (field.Parent is DockPanel previous)
                    previous.Children.Remove(field);
                row.Children.Add(field);
                steps.Children.Add(row);
            }
            Validate();
        }
        void AddField(string text)
        {
            var field = new TextBox { Text = text, MinWidth = 105 };
            field.TextChanged += (_, _) => Validate();
            fields.Add(field);
        }
        var initial = current.Split([',', '，'], StringSplitOptions.TrimEntries);
        foreach (var chord in initial)
            AddField(chord);
        if (initial.Length == 1)
            AddField("");
        else
            input.Text = initial[0];
        add.Click += (_, _) => { AddField(""); RefreshSteps(); fields[^1].Focus(); };
        mode.SelectionChanged += (_, _) => Validate();
        input.TextChanged += (_, _) => Validate();
        accept.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            var first = mode.SelectedIndex == 1 ? fields[0] : input;
            first.Focus();
            first.SelectAll();
        };
        RefreshSteps();
        return window.ShowDialog() == true ? value : null;
    }
}
