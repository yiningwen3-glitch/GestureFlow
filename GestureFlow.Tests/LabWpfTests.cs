using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GestureFlow.Core;
using GestureFlow.Desktop;
using GestureFlow.Desktop.Applications;
using GestureFlow.Lab;
using GestureFlow.Profiles;
using GestureFlow.UI;
using MainWindow = GestureFlow.Lab.MainWindow;
using WindowsInputNative = GestureFlow.Desktop.Input.WindowsInputNative;

namespace GestureFlow.Tests;

public sealed class LabWpfTests
{
    [Theory]
    [InlineData(Key.S, ModifierKeys.Control, ShortcutKey.S, ShortcutModifiers.Control)]
    [InlineData(Key.D4, ModifierKeys.Control | ModifierKeys.Shift, ShortcutKey.Digit4, ShortcutModifiers.Control | ShortcutModifiers.Shift)]
    [InlineData(Key.Oem7, ModifierKeys.Alt, ShortcutKey.OemQuote, ShortcutModifiers.Alt)]
    [InlineData(Key.F24, ModifierKeys.None, ShortcutKey.F24, ShortcutModifiers.None)]
    [InlineData(Key.LWin, ModifierKeys.Windows, (ShortcutKey)0, ShortcutModifiers.None)]
    [InlineData(Key.LeftShift, ModifierKeys.Shift, (ShortcutKey)0, ShortcutModifiers.None)]
    [InlineData(Key.MediaPlayPause, ModifierKeys.None, (ShortcutKey)0, ShortcutModifiers.None)]
    [InlineData(Key.S, (ModifierKeys)128, (ShortcutKey)0, ShortcutModifiers.None)]
    public void ShortcutCaptureRequiresASupportedPrimaryKey(
        Key key, ModifierKeys modifiers, ShortcutKey expected, ShortcutModifiers expectedModifiers)
    {
        var shortcut = ShortcutRecorder.Capture(key, modifiers);
        Assert.Equal(expected == 0 ? null : new KeyboardShortcut(expected, expectedModifiers), shortcut);
    }

    [Fact]
    public Task LabWindowEditsBindingsAndPreviewsScenariosUsingRoutedEvents() => RunOnSta(async () =>
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001
        app.ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001
        var directory = Directory.CreateTempSubdirectory("GestureFlow-Wpf-");
        var window = new MainWindow(directory.FullName) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            Assert.NotNull(window.TryFindResource("CardBackgroundFillColorDefaultBrush"));
            Assert.NotNull(window.TryFindResource("CardStrokeColorDefaultBrush"));
            Assert.NotNull(window.TryFindResource("ControlFillColorDefaultBrush"));
            Assert.NotNull(window.TryFindResource("ControlStrokeColorDefaultBrush"));

            var tabs = Named<TabControl>(window, "Tabs");
            tabs.SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var rows = Named<ItemsControl>(window, "BindingRows");
            var name = Descendants<TextBox>(rows).First();
            name.SetCurrentValue(TextBox.TextProperty, "");
            Assert.False(Named<Button>(window, "SaveProfileButton").IsEnabled);
            name.SetCurrentValue(TextBox.TextProperty, "快速保存");
            Assert.True(Named<Button>(window, "SaveProfileButton").IsEnabled);
            var recordKey = Descendants<Button>(rows)
                .Single(button => AutomationProperties.GetName(button) == "录制向上的快捷键");
            Click(recordKey);
            Assert.Equal("按下组合键…", recordKey.Content);
            SendKey(recordKey, Key.F8, Keyboard.PreviewKeyDownEvent);
            SendKey(recordKey, Key.F9, Keyboard.PreviewKeyDownEvent);
            SendKey(recordKey, Key.F8, Keyboard.PreviewKeyUpEvent);
            Assert.Equal("按下组合键…", recordKey.Content);
            SendKey(recordKey, Key.F9, Keyboard.PreviewKeyUpEvent);
            Assert.Equal("F8+F9", recordKey.Content);

            var mode = Named<ComboBox>(window, "ModePicker");
            mode.SelectedIndex = 1;
            Assert.Equal(8, rows.Items.Count);
            Assert.Equal("8 方向轮盘", Named<TextBlock>(window, "ModeLabel").Text);

            var wheel = Named<RadialWheel>(window, "Wheel");
            Named<ComboBox>(window, "ThemePicker").SelectedIndex = 1;
            Assert.True(wheel.IsDark);
            Named<CheckBox>(window, "ShowShortcuts").IsChecked = false;
            Assert.False(wheel.ShowShortcuts);

            AssertWheelRendersAt150Percent();
            await CaptureOverlayForReview();

            tabs.SelectedIndex = 0;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var surface = Named<GestureSurface>(window, "Surface");
            var rightButton = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            {
                RoutedEvent = Mouse.MouseDownEvent
            };
            surface.RaiseEvent(rightButton);
            Assert.False(rightButton.Handled);
            Assert.Equal("虚拟事件：—", Named<TextBlock>(window, "InputText").Text);

            var record = Named<Button>(window, "RecordButton");
            Click(record);
            Click(Descendants<Button>(tabs).Single(button => Equals(button.Tag, "North")));
            await WaitForPlayback(window);
            Click(record);
            Assert.Equal("已选择：快速保存", Named<TextBlock>(window, "SelectionText").Text);
            Assert.Equal("模拟快捷键：F8+F9", Named<TextBlock>(window, "ShortcutText").Text);
            Assert.True(Named<Button>(window, "SaveCaseButton").IsEnabled);

            Click(Named<Button>(window, "ReplayButton"));
            await WaitForPlayback(window);
            Assert.Equal("已选择：快速保存", Named<TextBlock>(window, "SelectionText").Text);
            Assert.Equal("模拟快捷键：F8+F9", Named<TextBlock>(window, "ShortcutText").Text);
            Assert.Contains("回放完成", Named<TextBlock>(window, "CaseStatus").Text);

            Click(Descendants<Button>(tabs).Single(button => Equals(button.Tag, "Cancel")));
            await WaitForPlayback(window);
            Assert.Equal("已取消手势", Named<TextBlock>(window, "OutcomeText").Text);

            tabs.SelectedIndex = 3;
            var virtualApps = Named<ComboBox>(window, "VirtualAppPicker");
            Assert.Equal(5, virtualApps.Items.Count);
            var status = Named<TextBlock>(window, "VirtualAppStatus");
            Assert.Equal("未启用", status.Text);
            virtualApps.SelectedIndex = 2;
            Assert.Equal("已启用", status.Text);
            CheckVirtualProfiles(window, directory.FullName);
            virtualApps.SelectedIndex = 0;
            Click(Named<Button>(window, "VirtualEnable"));
            Assert.Equal("已启用", status.Text);
            Click(Named<Button>(window, "VirtualStartInput"));
            Click(Named<Button>(window, "VirtualPause"));
            Assert.Equal("已暂停", status.Text);
            Assert.Contains("已取消", Named<TextBlock>(window, "VirtualInputResult").Text);
            Click(Named<Button>(window, "VirtualPause"));
            Click(Named<Button>(window, "VirtualRevoke"));
            Assert.Equal("未启用", status.Text);

            await CheckLabDock(window, directory.FullName);
            window.Close();
            Assert.Empty(Application.Current.Windows.OfType<FloatingDockWindow>());
            await CheckDesktopProfiles(directory.FullName);
        }
        finally
        {
            if (window.IsLoaded)
                window.Close();
            directory.Delete(recursive: true);
        }
    });

    private static async Task CheckLabDock(MainWindow window, string directory)
    {
        var apps = Named<ComboBox>(window, "DockLabApp");
        Assert.Equal(4, apps.Items.Count);
        Named<TabControl>(window, "Tabs").SelectedItem = Named<TabItem>(window, "DockLabTab");
        var status = Named<TextBlock>(window, "DockLabStatus");
        var preview = Named<Button>(window, "DockLabPreview");
        var output = Named<TextBlock>(window, "DockLabResult");
        var visibility = Named<Button>(window, "DockLabVisibility");
        var catalogPath = Path.Combine(directory, GestureProfileManager.FileName);
        var before = File.ReadAllText(catalogPath);
        Click(visibility);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var dock = Assert.Single(Application.Current.Windows.OfType<FloatingDockWindow>());
        Assert.False(dock.ShowActivated);
        Assert.False(dock.ShowInTaskbar);
        Assert.False(dock.IsActive);
        Assert.False(dock.IsExpanded);
        Assert.Equal("模拟通用", Named<TextBlock>(dock, "CompactProfile").Text);
        apps.SelectedIndex = 2;
        Assert.Contains("VS Code · 常用", status.Text);
        Click(Named<Button>(dock, "ExpandButton"));
        Assert.True(dock.IsExpanded);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        var optionButtons = Descendants<Button>(Named<ItemsControl>(dock, "DockProfiles")).ToArray();
        Click(optionButtons.Single(button => AutomationProperties.GetName(button) == "VS Code · 8 方向"));
        Assert.Contains("VS Code · 8 方向", status.Text);
        Assert.False(dock.IsExpanded);
        Click(Named<Button>(dock, "ExpandButton"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Click(Descendants<Button>(Named<ItemsControl>(dock, "DockProfiles"))
            .Single(button => AutomationProperties.GetName(button) == "使用通用配置"));
        Assert.Contains("模拟通用", status.Text);
        Assert.False(dock.IsExpanded);
        Click(Named<Button>(dock, "ExpandButton"));
        Click(preview);
        Assert.Contains("模拟执行", output.Text);
        Click(Named<Button>(dock, "DockPause"));
        Assert.Contains("已暂停", status.Text);
        Click(preview);
        Assert.Contains("无命令", output.Text);
        Click(Named<Button>(dock, "DockPause"));
        apps.SelectedIndex = 1;
        Assert.Contains("已禁用", status.Text);
        Assert.Contains("SolidWorks · 常用", status.Text);
        Click(preview);
        Assert.Contains("无命令", output.Text);
        apps.SelectedIndex = 3;
        Assert.Contains("Photoshop · 常用", status.Text);
        Click(preview);
        Assert.Contains("无命令", output.Text);
        Click(Named<Button>(dock, "ExpandButton"));
        Assert.False(dock.IsExpanded);
        Click(Named<Button>(dock, "DockHide"));
        Assert.False(dock.IsVisible);
        for (var i = 0; i < 8; i++)
        {
            Click(visibility);
            Assert.True(dock.IsVisible);
            Click(visibility);
            Assert.False(dock.IsVisible);
        }
        Assert.Same(dock, Assert.Single(Application.Current.Windows.OfType<FloatingDockWindow>()));
        Assert.Equal(before, File.ReadAllText(catalogPath));
        Assert.False(File.Exists(Path.Combine(directory, "application-policies.json")));
        Assert.False(FloatingDockPreferences.Load(Path.Combine(directory, "lab-dock.json"), out _).Visible);
    }

    private static void CheckVirtualProfiles(MainWindow window, string directory)
    {
        var saved = GestureProfileManager.Open(directory);
        var global = saved.Create("全局预览", select: true);
        var alternate = saved.Create("备用通用");
        var app = saved.Create("Word 专属", "virtual:word", select: true);
        saved.Create("Word 备用", "virtual:word");
        saved.Create("SolidWorks 专属", "virtual:solidworks", select: true);
        var apps = Named<ComboBox>(window, "VirtualAppPicker");
        var appProfiles = Named<ComboBox>(window, "VirtualAppProfilePicker");
        var globals = Named<ComboBox>(window, "VirtualGlobalProfilePicker");
        var profile = Named<TextBlock>(window, "VirtualProfileStatus");
        var output = Named<TextBlock>(window, "VirtualInputResult");
        var trigger = Named<Button>(window, "VirtualStartInput");
        var reload = Named<Button>(window, "ReloadVirtualProfiles");
        Click(reload);
        Assert.Contains("Word 专属", profile.Text);
        Assert.Equal(3, appProfiles.Items.Count);
        Click(trigger);
        appProfiles.SelectedIndex = 0;
        Assert.Contains("全局预览", profile.Text);
        Click(trigger);
        Assert.Contains("模拟执行 · Word 专属", output.Text);
        globals.SelectedItem = globals.Items.Cast<object>().Single(item => item.ToString()!.Contains("备用通用"));
        Assert.Contains("备用通用", profile.Text);
        apps.SelectedIndex = 3;
        Assert.Contains("备用通用", profile.Text);
        Click(trigger);
        apps.SelectedIndex = 0;
        Assert.Contains("已取消", output.Text);
        Click(trigger);
        Assert.Contains("输入放行", output.Text);
        Assert.Contains("当前生效方案 · 无", profile.Text);
        Assert.Equal(global.ProfileId, GestureProfileManager.Open(directory).Selected()!.ProfileId);
        Assert.Equal(app.ProfileId, GestureProfileManager.Open(directory).Selected("virtual:word")!.ProfileId);

        saved.Rename(global.ProfileId, "已修改通用");
        apps.SelectedIndex = 3;
        Click(reload);
        Assert.Contains("已修改通用", profile.Text);
        var path = Path.Combine(directory, GestureProfileManager.FileName);
        var original = File.ReadAllText(path);
        var catalog = JsonNode.Parse(original)!.AsObject();
        catalog["global"] = Guid.NewGuid().ToString();
        File.WriteAllText(path, catalog.ToJsonString());
        Click(reload);
        Assert.Contains("系统默认", profile.Text);
        File.WriteAllText(path, original);
        Assert.NotEqual(alternate.ProfileId, saved.Selected()!.ProfileId);
    }

    private static async Task CheckDesktopProfiles(string directory)
    {
        new DesktopSettingsStore(directory, 3).Save(new(
            DefaultGestureProfiles.Create(GestureMode.Four), CloseAction: WindowCloseAction.Hide,
            Theme: DesktopTheme.Light, WelcomeSeen: true));
        var window = new GestureFlow.Desktop.MainWindow(directory)
        {
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 840, Height = 550
        };
        try
        {
            window.Show();
            ((DesktopShellViewModel)window.DataContext).NavigateTo(DesktopPage.Gestures);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var picker = Named<ComboBox>(window, "ProfilePicker");
            picker.SelectedItem = GestureProfileManager.SystemDefault;
            Assert.False(Named<Button>(window, "EditProfileButton").IsEnabled);
            Named<TextBox>(window, "ProfileNameBox").Text = "桌面方案副本";
            Click(Named<Button>(window, "CopyProfileButton"));
            var copied = Assert.IsType<ManagedGestureProfile>(picker.SelectedItem);
            Assert.False(copied.ReadOnly);
            var editor = Named<GestureConfigurationView>(window, "GestureConfiguration");
            Named<ComboBox>(editor, "ModePicker").SelectedIndex = 1;
            editor.UpdateLayout();
            var shortcutButton = Descendants<Button>(editor)
                .Single(button => AutomationProperties.GetName(button) == "录制向上的快捷键");
            Click(shortcutButton);
            SendKey(shortcutButton, Key.F7, Keyboard.PreviewKeyDownEvent);
            SendKey(shortcutButton, Key.F8, Keyboard.PreviewKeyDownEvent);
            SendKey(shortcutButton, Key.F8, Keyboard.PreviewKeyUpEvent);
            SendKey(shortcutButton, Key.F7, Keyboard.PreviewKeyUpEvent);
            Click(Named<Button>(editor, "SaveGestureButton"));
            Click(Named<Button>(window, "SelectProfileButton"));
            picker.SelectedItem = GestureProfileManager.SystemDefault;
            picker.SelectedItem = picker.Items.Cast<ManagedGestureProfile>().Single(item => item.ProfileId == copied.ProfileId);
            Assert.Equal(GestureMode.Eight, editor.Model.Profile.Mode);
            Assert.Equal(copied.ProfileId, GestureProfileManager.Open(directory).Selected()!.ProfileId);
            Assert.Equal(new KeyboardShortcut(ShortcutKey.F7, ShortcutModifiers.None, [ShortcutKey.F8]),
                editor.Model.Profile.GetEnabledBinding(GestureDirection.North)?.Shortcut);
            Assert.Contains("桌面方案副本", Named<TextBlock>(window, "SelectedProfileText").Text);
            picker.SelectedItem = GestureProfileManager.SystemDefault;
            Click(Named<Button>(window, "SelectProfileButton"));
            picker.SelectedItem = picker.Items.Cast<ManagedGestureProfile>().Single(item => item.ProfileId == copied.ProfileId);
            picker.SelectedItem = GestureProfileManager.SystemDefault;
            Named<ComboBox>(editor, "ModePicker").SelectedIndex = 1;
            Click(Named<Button>(editor, "SaveGestureButton"));
            var directlySaved = GestureProfileManager.Open(directory).Selected()!;
            Assert.Equal("我的桌面笔势", directlySaved.Name);
            Assert.Equal(GestureMode.Eight, directlySaved.Configuration.Mode);
            Assert.Equal(GestureMode.Four, GestureProfileManager.SystemDefault.Configuration.Mode);
            var shell = (DesktopShellViewModel)window.DataContext;
            Assert.Equal(["首页", "我的笔势", "应用管理"], shell.PrimaryPages.Select(page => page.Title));
            Assert.True(shell.CanPause, shell.InputMessage);
            Assert.Equal("仅 Windows 桌面手势已开启", shell.InputState);
            await CheckDesktopDock(window, directory);
            await CheckInstalledOfficeIsListed(window, shell);
        }
        finally
        {
            window.CloseRuntime();
            window.Close();
        }
        Assert.Empty(Application.Current.Windows.OfType<FloatingDockWindow>());
    }

    private static async Task CheckInstalledOfficeIsListed(GestureFlow.Desktop.MainWindow window, DesktopShellViewModel shell)
    {
        var office = await new OfficeApplicationSource().DiscoverAsync(CancellationToken.None);
        if (office.Applications.Count == 0)
            return;

        shell.NavigateTo(DesktopPage.Applications);
        var refresh = Named<Button>(window, "RefreshButton");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!refresh.IsEnabled)
            await Task.Delay(50, timeout.Token);

        var apps = Named<ListBox>(window, "ApplicationList");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < apps.Items.Count; i++)
        {
            apps.SelectedIndex = i;
            if (Named<TextBlock>(window, "DetailSource").Text.Contains("Microsoft Office", StringComparison.Ordinal))
                paths.Add(Named<TextBlock>(window, "DetailPath").Text);
        }
        Assert.All(office.Applications, app => Assert.Contains($"可执行文件  {app.ExecutablePath}", paths));
    }

    private static void SendKey(UIElement target, Key key, RoutedEvent routedEvent)
    {
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key)
        {
            RoutedEvent = routedEvent
        };
        target.RaiseEvent(input);
        Assert.True(input.Handled);
    }

    private static async Task CheckDesktopDock(GestureFlow.Desktop.MainWindow window, string directory)
    {
        var shell = (DesktopShellViewModel)window.DataContext;
        shell.NavigateTo(DesktopPage.Settings);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Named<Expander>(window, "DockSettingsExpander").IsExpanded = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var visible = Named<CheckBox>(window, "DockVisibleSetting");
        var topmost = Named<CheckBox>(window, "DockTopmostSetting");
        var collapsed = Named<CheckBox>(window, "DockCollapsedSetting");
        var auto = Named<CheckBox>(window, "DockAutoCollapseSetting");
        var path = Path.Combine(directory, "floating-dock.json");

        Assert.False(visible.IsChecked);
        SetChecked(auto, false);
        SetChecked(collapsed, false);
        SetChecked(visible, true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var dock = Assert.Single(Application.Current.Windows.OfType<FloatingDockWindow>());
        var dockHandle = new WindowInteropHelper(dock).Handle;
        Assert.True(dock.IsVisible);
        Assert.True(dock.IsExpanded);
        Assert.True(dock.Topmost);
        Assert.False(dock.IsActive);
        Assert.False(dock.ShowInTaskbar);
        Assert.True(Named<Button>(dock, "DockPause").IsEnabled);
        Assert.Equal((nint)3, SendMessage(dockHandle, 0x0021, 0, 0));
        Assert.Equal((nint)0x08000080, GetWindowLongPtr(dockHandle, -20) & 0x08000080);

        SetChecked(topmost, false);
        Named<ComboBox>(window, "DockOpacitySetting").SelectedValue = 0.75;
        Assert.False(dock.Topmost);
        Assert.Equal(0.75, dock.Opacity);
        Click(Named<Button>(window, "DockResetPositionButton"));
        Assert.Equal(new FloatingDockPreferences(true, false, false, false, 0.75),
            FloatingDockPreferences.Load(path, out _));

        var card = Named<Border>(dock, "DockCard");
        var light = Assert.IsType<SolidColorBrush>(card.Background).Color;
        shell.SelectedTheme = shell.Themes.Single(option => option.Theme == DesktopTheme.Dark);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.NotEqual(light, Assert.IsType<SolidColorBrush>(card.Background).Color);

        dock.IsHitTestVisible = false;
        Assert.False(dock.IsMouseOver);
        SetChecked(auto, true);
        await Task.Delay(TimeSpan.FromMilliseconds(2200));
        Assert.False(dock.IsExpanded);
        dock.IsHitTestVisible = true;

        var handle = new WindowInteropHelper(window).Handle;
        SendMessage(handle, 0x0218, 4, 0);
        SendMessage(handle, 0x0218, 7, 0);
        SendMessage(handle, WindowsInputNative.RegisterWindowMessage("TaskbarCreated"), 0, 0);
        SendMessage(handle, 0x007E, 0, 0);
        Assert.True(dock.IsVisible);
        Assert.Same(dock, Assert.Single(Application.Current.Windows.OfType<FloatingDockWindow>()));
        Assert.False(shell.CanPause);
        window.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(window.IsVisible);
        Assert.True(dock.IsVisible);
        Click(Named<Button>(dock, "ExpandButton"));
        Click(Named<Button>(dock, "DockSettings"));
        Assert.True(window.IsVisible);
        Assert.Equal(DesktopPage.Settings, shell.CurrentPage!.Page);
        Click(Named<Button>(dock, "DockHide"));
        Assert.False(dock.IsVisible);
        Assert.False(FloatingDockPreferences.Load(path, out _).Visible);
        Assert.False(shell.CanPause);
    }

    private static void SetChecked(CheckBox checkBox, bool value)
    {
        checkBox.IsChecked = value;
        checkBox.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    private static async Task CaptureOverlayForReview()
    {
        var folder = Environment.GetEnvironmentVariable("GESTUREFLOW_WHEEL_CAPTURE");
        if (folder is null)
            return;
        Directory.CreateDirectory(folder);
        var overlay = new GestureFlow.Desktop.Input.GestureWheelWindow();
        try
        {
            foreach (var mode in new[] { GestureMode.Four, GestureMode.Eight })
            {
                var point = new GesturePoint(400, 300);
                var profile = DefaultGestureProfiles.Create(mode);
                var foreground = WindowsInputNative.GetForegroundWindow();
                overlay.Open(GestureFlow.Desktop.Input.GestureWheelWindow.Locate(point), profile,
                    GestureWheelMetrics.DefaultTriggerDistance);
                await Task.Delay(150);
                Assert.False(overlay.IsActive);
                Assert.Equal(foreground, WindowsInputNative.GetForegroundWindow());
                var wheel = (RadialWheel)overlay.Content;
                wheel.Show(profile, GestureDirection.South);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var dpi = VisualTreeHelper.GetDpi(wheel);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(wheel.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(wheel.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(wheel);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(folder, $"wheel-{(int)mode}.png"));
                encoder.Save(output);
            }
        }
        finally { overlay.Close(); }
    }

    private static void AssertWheelRendersAt150Percent()
    {
        var wheel = new RadialWheel { IsDark = true };
        wheel.Show(GestureFlow.Profiles.DefaultGestureProfiles.Create(GestureMode.Eight), GestureDirection.South);
        wheel.Measure(new Size(320, 320));
        wheel.Arrange(new Rect(0, 0, 320, 320));
        wheel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(480, 480, 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(wheel);

        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(240, 90, 1, 1), pixel, 4, 0);
        Assert.Equal(new byte[] { 241, 246, 247, 255 }, pixel);
        bitmap.CopyPixels(new Int32Rect(240, 390, 1, 1), pixel, 4, 0);
        Assert.Equal(new byte[] { 190, 191, 190, 255 }, pixel);
        bitmap.CopyPixels(new Int32Rect(240, 240, 1, 1), pixel, 4, 0);
        Assert.Equal(0, pixel[3]);
    }

    private static async Task WaitForPlayback(Window window)
    {
        var stop = Named<Button>(window, "StopPlayback");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (stop.IsEnabled)
            await Task.Delay(20, timeout.Token);
    }

    private static T Named<T>(FrameworkElement root, string name) => Assert.IsType<T>(root.FindName(name));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<T> Descendants<T>(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;

            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static Task RunOnSta(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Exception? failure = null;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
            if (failure is null)
                completion.SetResult();
            else
                completion.SetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
