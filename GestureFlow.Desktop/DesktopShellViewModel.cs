using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GestureFlow.Desktop;

internal enum DesktopPage { Home, Gestures, Applications, Lab, Settings, Help, About }
internal enum DesktopTheme { System, Light, Dark }

internal sealed record NavigationItem(DesktopPage Page, string Title, string Icon);

internal sealed record ThemeOption(DesktopTheme Theme, string Label);

internal sealed class DesktopShellViewModel : INotifyPropertyChanged
{
    public IReadOnlyList<NavigationItem> Pages { get; } =
    [
        new(DesktopPage.Home, "首页", "\uE80F"),
        new(DesktopPage.Gestures, "我的笔势", "\uE9F3"),
        new(DesktopPage.Applications, "应用管理", "\uE71D"),
        new(DesktopPage.Lab, "虚拟模拟器", "\uE7FC"),
        new(DesktopPage.Settings, "设置", "\uE713"),
        new(DesktopPage.Help, "使用帮助", "\uE897"),
        new(DesktopPage.About, "关于软件", "\uE946")
    ];

    public IEnumerable<NavigationItem> PrimaryPages => Pages.Where(item =>
        item.Page is DesktopPage.Home or DesktopPage.Gestures or DesktopPage.Applications);

    public DesktopInformation Information { get; } = new();

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(DesktopTheme.System, "跟随系统"),
        new(DesktopTheme.Light, "浅色"),
        new(DesktopTheme.Dark, "深色")
    ];

    private NavigationItem? _currentPage;
    private ThemeOption? _selectedTheme;
    private string _inputState = "手势监听已关闭";
    private string _inputMessage = "";
    private string _inputAction = "启用桌面手势";
    private string _pauseAction = "紧急暂停";
    private string _foregroundSummary = "等待可识别的其他前台应用";
    private bool _canPause;
    private bool _canOpenForeground;

    public DesktopShellViewModel()
    {
        _currentPage = Pages[0];
        _selectedTheme = Themes[0];
    }

    public NavigationItem? CurrentPage
    {
        get => _currentPage;
        set { if (value is not null) Set(ref _currentPage, value); }
    }

    public ThemeOption? SelectedTheme
    {
        get => _selectedTheme;
        set { if (value is not null) Set(ref _selectedTheme, value); }
    }

    public string InputState { get => _inputState; private set => Set(ref _inputState, value); }
    public string InputMessage { get => _inputMessage; set => Set(ref _inputMessage, value); }
    public string InputAction { get => _inputAction; private set => Set(ref _inputAction, value); }
    public string PauseAction { get => _pauseAction; private set => Set(ref _pauseAction, value); }
    public bool CanPause { get => _canPause; private set => Set(ref _canPause, value); }
    public string ForegroundSummary { get => _foregroundSummary; private set => Set(ref _foregroundSummary, value); }
    public bool CanOpenForeground { get => _canOpenForeground; private set => Set(ref _canOpenForeground, value); }

    public void NavigateTo(DesktopPage page) => CurrentPage = Pages.First(item => item.Page == page);

    public void UpdateInput(bool running, bool paused, bool testMode, bool globalEnabled)
    {
        InputState = (running, paused, testMode, globalEnabled) switch
        {
            (false, _, _, _) => "手势监听已关闭",
            (true, true, true, _) => "测试已暂停",
            (true, _, true, _) => "仅测试窗口监听中",
            (true, true, false, _) => "手势已暂停",
            (true, false, false, true) => "全局输入已开启",
            _ => "仅 Windows 桌面手势已开启"
        };
        InputAction = (running, testMode, globalEnabled) switch
        {
            (false, _, _) => "启用桌面手势",
            (true, true, _) => "停止测试输入",
            (true, false, true) => "仅保留桌面手势",
            _ => "开启全局输入…"
        };
        PauseAction = paused ? "恢复手势" : "暂停手势";
        CanPause = running;
    }

    public void UpdateForeground(string summary, bool matched)
    {
        ForegroundSummary = summary;
        CanOpenForeground = matched;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
