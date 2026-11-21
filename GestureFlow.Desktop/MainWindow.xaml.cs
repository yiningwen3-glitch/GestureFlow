using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop.Applications;
using GestureFlow.UI;
using Microsoft.Win32;
using ApplicationIdentity = GestureFlow.Desktop.Applications.ApplicationIdentity;

namespace GestureFlow.Desktop;

public partial class MainWindow : Window
{
    private sealed record ApplicationRow(InstalledApplication Application, ImageSource? Icon)
    {
        public string Status => Application.GestureEnabled ? "已启用" : "未启用";
    }

    private readonly ApplicationStateStore _store;
    private static readonly AppRiskAssessment SideButtonPolicy = new(RiskLevel.Compatible, "",
        "GestureFlow", "1", "side-button-v1", GestureTrigger.MouseX1);
    private readonly ProtectionPolicyService _protection = new();
    private readonly InstalledApplicationDiscoveryService _discovery;
    private readonly ForegroundApplicationService _foreground = new();
    private readonly DispatcherTimer _foregroundTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DesktopShellViewModel _shell = new();
    private IReadOnlyList<ApplicationRow> _rows = [];
    private IReadOnlyDictionary<string, ApplicationOverride> _policies = new Dictionary<string, ApplicationOverride>();
    private IReadOnlyDictionary<string, GestureAuthorization> _authorizations = new Dictionary<string, GestureAuthorization>();
    private ICollectionView? _view;
    private readonly BusinessForegroundContext _businessForeground = new(Environment.ProcessId);
    private RunningApplication? LastForeground => _businessForeground.Target;
    private bool _refreshing;
    private bool _checkingForeground;
    private bool _closed;
    private bool _policiesReadable;
    private bool _manualReadable;

    public MainWindow() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GestureFlow")) { }

    internal MainWindow(string settingsDirectory)
    {
        _store = new(settingsDirectory);
        _settingsStore = new(settingsDirectory, Hotkeys.Length);
        _dockPreferencesPath = Path.Combine(settingsDirectory, "floating-dock.json");
        _desktopInput = new(_foreground, AuthorizeInput, () => _settings.SideButton, ObserveForeground, ReportInput, DesktopInputStopped);
        _discovery = new([new RegistryApplicationSource(), new WindowsShellApplicationSource(),
            new OfficeApplicationSource(), new RunningApplicationSource()],
            File.Exists, (_, _, _) => RiskLevel.Compatible,
            app => VerifiedAuthorization(app, TargetFor(app)) is not null, ExecutableInstallationStamp.Read);
        InitializeComponent();
        _shell.PropertyChanged += OnShellChanged;
        DataContext = _shell;
        Loaded += OnLoaded;
        Activated += (_, _) => ShowForeground();
        Deactivated += (_, _) => ShowForeground();
        _foregroundTimer.Tick += OnForegroundTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        InitializeDesktopSettings();
        InitializeDesktopInput();
        InitializeFloatingDock();
        StartDesktopInput();
        _foregroundTimer.Start();
        var refresh = RefreshAsync();
        await Dispatcher.Yield(DispatcherPriority.Background);
        var openApplications = !_closed && IsVisible && _closeDialog is null && !_settings.WelcomeSeen && ShowWelcomeWizard();
        await refresh;
        if (openApplications && !_closed)
            _shell.NavigateTo(DesktopPage.Applications);
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseRuntime();
        base.OnClosed(e);
    }

    internal void CloseRuntime()
    {
        if (_closed)
            return;
        _closed = true;
        _shell.PropertyChanged -= OnShellChanged;
        _dock?.Close();
        DisposeDesktopInput();

        _foregroundTimer.Stop();
        _foregroundTimer.Tick -= OnForegroundTick;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesktopShellViewModel.CurrentPage))
        {

            if (_shell.CurrentPage?.Page == DesktopPage.Settings && _settingsLoaded)
                LoadStartupOption();
            UpdateInformationPage();
        }
        else if (e.PropertyName == nameof(DesktopShellViewModel.SelectedTheme))
            DesktopThemeChanged();
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync(string? preferredId = null)
    {
        if (_refreshing || _closed)
            return;

        var priorSelection = preferredId ?? SelectedApplication?.AppId;

        _refreshing = true;
        _desktopInput.Invalidate();
        SetButtonStates();
        StatusText.Text = "正在读取桌面快捷方式和常用办公软件…";
        var token = _lifetime.Token;
        try
        {
            var warnings = new List<string>();
            _policies = new Dictionary<string, ApplicationOverride>();
            _authorizations = new Dictionary<string, GestureAuthorization>();
            IReadOnlyList<ManualApplication> manual = [];
            _policiesReadable = _manualReadable = false;
            try
            {
                _policies = _store.LoadPolicies();
                _authorizations = _store.LoadAuthorizations();
                _policiesReadable = true;
            }
            catch (Exception error) when (IsReadError(error))
            {
                warnings.Add("应用启用记录读取失败，暂时无法更改启用状态。");
            }
            try
            {
                manual = _store.LoadManualApplications();
                _manualReadable = true;
            }
            catch (Exception error) when (IsReadError(error))
            {
                warnings.Add("手动应用清单读取失败，添加和关联已暂停。");
            }

            var catalog = await _discovery.DiscoverAsync(manual, _policies, token);
            var icons = await Task.Run(() => ApplicationIconLoader.Load(catalog.Applications.Where(IsListedApplication), token), token);
            if (_closed)
                return;

            ShowRows(catalog.Applications.Select(app => new ApplicationRow(app, icons.GetValueOrDefault(app.AppId))).ToArray(),
                priorSelection);
            warnings.AddRange(catalog.Warnings);
            StatusText.Text = warnings.Count == 0 ? "应用清单已更新" : string.Join(" ", warnings);
            ShowForeground();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsReadError(error))
        {
            if (!_closed)
                StatusText.Text = $"刷新失败 · {error.Message}";
        }
        finally
        {
            _refreshing = false;
            if (!_closed)
            {
                SetButtonStates();
                ShowForeground();
            }
        }
    }

    private InstalledApplication? SelectedApplication => (ApplicationList.SelectedItem as ApplicationRow)?.Application;

    private void ShowRows(IReadOnlyList<ApplicationRow> rows, string? selectedId)
    {
        _rows = rows;
        var previousScope = _profileAppId;
        RefreshProfileScopes();
        if (_profileAppId != previousScope)
            ShowProfiles();

        _view = CollectionViewSource.GetDefaultView(rows);
        _view.Filter = FilterRow;
        ApplicationList.ItemsSource = _view;
        if (selectedId is not null)
            ApplicationList.SelectedItem = rows.FirstOrDefault(row => row.Application.AppId == selectedId && FilterRow(row))
                ?? rows.FirstOrDefault(row => row.Application.ExecutablePath is { } path
                    && ApplicationIdentity.ForPath(path) == selectedId && FilterRow(row));
        UpdateCount();
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (_view is null)
            return;

        _view.Refresh();
        UpdateCount();
    }

    private bool FilterRow(object item)
    {
        var app = ((ApplicationRow)item).Application;
        var query = SearchBox.Text.Trim();
        return IsListedApplication(app) && (query.Length == 0 || app.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            && (EnabledFilter.SelectedIndex <= 0 || app.GestureEnabled == (EnabledFilter.SelectedIndex == 1));
    }

    private void UpdateCount() => CountText.Text =
        $"显示 {ApplicationList.Items.Count} / {_rows.Count(row => IsListedApplication(row.Application))} 个应用";

    internal static bool IsListedApplication(InstalledApplication app) =>
        (app.DiscoverySource & (DiscoverySource.Desktop | DiscoverySource.Office)) != 0;

    private void ApplicationSelected(object sender, SelectionChangedEventArgs e)
    {

        RefreshApplicationDetails();
    }

    private void RefreshApplicationDetails()
    {
        var app = SelectedApplication;
        var profile = app is null ? null : _profiles.Selected(app.AppId);
        DetailProfile.Text = profile is null ? "方案 · 跟随桌面" : $"方案 · {profile.Name}";
        AppProfileButton.Content = profile is null ? "自定义" : "编辑方案";
        AppProfileButton.Visibility = app?.IdentificationStatus == IdentificationStatus.Matched ? Visibility.Visible : Visibility.Collapsed;
        EnableAppInputButton.Visibility = app?.GestureEnabled == true && !_globalInputEnabled && !_desktopInput.IsTestMode
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyDetails.Visibility = app is null ? Visibility.Visible : Visibility.Collapsed;
        Details.Visibility = app is null ? Visibility.Collapsed : Visibility.Visible;
        if (app is not null)
        {
            DetailTitle.Text = app.DisplayName;
            DetailSource.Text = $"来源  {SourceText(app.DiscoverySource)}";
            DetailIdentity.Text = app.IdentificationStatus == IdentificationStatus.Matched ? "身份已匹配" : "身份待关联";
            DetailPath.Text = $"可执行文件  {app.ExecutablePath ?? app.ExecutableName ?? "路径不可用"}";
            DetailPackage.Text = app.Aumid is not null || app.PackageFamilyName is not null
                ? $"包身份  {app.Aumid ?? app.PackageFamilyName}" : "";
            DetailPackage.Visibility = DetailPackage.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            ShowAuthorization(app);
        }

        SetButtonStates();
    }

    private void SetButtonStates()
    {
        var app = SelectedApplication;
        RefreshButton.IsEnabled = !_refreshing;
        var edit = !_refreshing && _policiesReadable && app?.IdentificationStatus == IdentificationStatus.Matched;
        EnableButton.IsEnabled = edit && app?.GestureEnabled == false && TargetFor(app) is not null;
        RevokeButton.IsEnabled = edit && app?.UserOverride is { Allowed: true };
        AssociateButton.Visibility = app is { IdentificationStatus: IdentificationStatus.Pending, PackageFamilyName: null, ExecutablePath: null }
            && (app.DiscoverySource & ~DiscoverySource.Running) != 0 ? Visibility.Visible : Visibility.Collapsed;
        AssociateButton.IsEnabled = !_refreshing && _manualReadable;
    }

    private async void AssociateClick(object sender, RoutedEventArgs e)
    {
        if (SelectedApplication is { IdentificationStatus: IdentificationStatus.Pending } app)
            await AddApplicationAsync(app);
    }

    private async Task AddApplicationAsync(InstalledApplication associated)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"为 {associated.DisplayName} 选择可执行文件",
            Filter = "Windows 程序 (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var path = ApplicationIdentity.NormalizeExecutable(dialog.FileName);
        if (path is null || !File.Exists(path))
        {
            StatusText.Text = "请选择存在且可以访问的 .exe 文件。";
            return;
        }

        try
        {
            _store.AddManualApplication(new(path, associated.AppId));
            await RefreshAsync(ApplicationIdentity.ForPath(path));
        }
        catch (Exception error) when (IsReadError(error))
        {
            StatusText.Text = $"保存应用失败 · {error.Message}";
        }
    }

    private static ProtectionTarget? TargetFor(InstalledApplication? app) =>
        app is { IdentificationStatus: IdentificationStatus.Matched, InstallationStamp: { } stamp }
        && ApplicationIdentity.IsManageableId(app.AppId)
            ? new(app.AppId, app.ExecutablePath, SideButtonPolicy, stamp) : null;

    private GestureAuthorization? VerifiedAuthorization(InstalledApplication app, ProtectionTarget? target)
    {
        if (target is null || app.UserOverride is not { Allowed: true, HasUserAuthorization: true }
            || !_authorizations.TryGetValue(target.AppId, out var previous)
            || previous.InstallationStamp != target.InstallationStamp
            || !string.Equals(previous.ExecutablePath, target.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return null;

        // Existing explicit opt-ins remain valid for the same installation regardless of their legacy trigger.
        return previous with { PolicyStamp = SideButtonPolicy.PolicyStamp, Trigger = _settings.SideButton };
    }

    private void ShowAuthorization(InstalledApplication app)
    {
        var target = TargetFor(app);
        var grant = VerifiedAuthorization(app, target);
        AuthorizationStatus.Text = target is null ? "需要核对本地程序及其安装标识。"
            : grant is null ? "未启用"
            : _protection.IsPaused ? "已暂停" : $"已启用 · {ProtectionLabels.Trigger(_settings.SideButton)}";
    }

    private void SecondaryPageClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<DesktopPage>(tag, out var page))
            _shell.NavigateTo(page);
    }

    private void EnableAppInputClick(object sender, RoutedEventArgs e) => RequestGlobalInput();

    private void EnableClick(object sender, RoutedEventArgs e)
    {
        if (!EnableButton.IsEnabled || SelectedApplication is not { } app || TargetFor(app) is not { } target)
            return;

        if (ExecutableInstallationStamp.Read(target.ExecutablePath) != target.InstallationStamp)
        {
            StatusText.Text = "程序安装标识已变化。请刷新应用清单后重新启用。";
            return;
        }

        UpdateAuthorization(app, () => _store.EnableSideButton(target, _settings.SideButton), "已启用");
    }

    private void RevokeClick(object sender, RoutedEventArgs e)
    {
        if (SelectedApplication is not { UserOverride: { } current } app)
            return;

        var disabled = current with { Allowed = false, HasUserAuthorization = false };
        UpdateAuthorization(app, () => _store.SavePolicy(app.AppId, disabled), "已停用");
    }

    private void UpdateAuthorization(InstalledApplication app,
        Func<IReadOnlyDictionary<string, ApplicationOverride>> save, string result)
    {
        _desktopInput.Invalidate();
        try
        {
            _policies = save();
            _authorizations = _store.LoadAuthorizations();
            var updated = app with { UserOverride = _policies[app.AppId] };
            updated = updated with { GestureEnabled = VerifiedAuthorization(updated, TargetFor(updated)) is not null };
            ShowRows(_rows.Select(row => row.Application.AppId == app.AppId ? row with { Application = updated } : row).ToArray(), app.AppId);
            StatusText.Text = $"{app.DisplayName} · {result}";
            ShowForeground();
        }
        catch (Exception error) when (IsReadError(error))
        {
            SetPaused(true);
            StatusText.Text = $"保存应用启用状态失败 · {error.Message}";
        }
    }

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        SetPaused(!_protection.IsPaused);
    }

    private void SetPaused(bool paused)
    {
        _protection.SetPaused(paused);
        _desktopInput.SetPaused(paused);
        UpdateInputControls();
        if (_desktopInput.IsRunning)
            ReportInput(_shell.InputState);
        if (SelectedApplication is { } app)
            ShowAuthorization(app);
        ShowForeground();
    }

    private async void OnForegroundTick(object? sender, EventArgs e)
    {
        if (!_desktopInput.IsRunning || _protection.IsPaused)
            await CheckForegroundAsync();
    }

    private async Task CheckForegroundAsync()
    {
        if (_checkingForeground || _closed)
            return;

        if (ForegroundApplicationService.ForegroundWindow().ProcessId == Environment.ProcessId)
        {
            ObserveForeground(null);
            return;
        }

        _checkingForeground = true;
        try
        {
            var running = await Task.Run(_foreground.GetForegroundApplication);
            ObserveForeground(running);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ExternalException)
        {
            ObserveForeground(null);
        }
        finally { _checkingForeground = false; }
    }

    private void ObserveForeground(RunningApplication? running)
    {
        if (_closed || !_businessForeground.Observe(running, ForegroundApplicationService.ForegroundWindow(),
            ForegroundApplicationService.IsLive))
            return;
        ShowForeground();
    }

    private ForegroundGestureTarget ResolveForeground(RunningApplication running)
    {
        var match = ForegroundApplicationService.Match(running, _rows.Select(row => row.Application));
        var target = match is not null && running.InstallationStamp is { } stamp
            && running.WindowKind != ForegroundWindowKind.ApplicationHost
                ? new ProtectionTarget(match.AppId, running.ExecutablePath, SideButtonPolicy, stamp) : null;
        var grant = _policiesReadable && !_refreshing && match is not null
            ? VerifiedAuthorization(match, target) : null;
        var status = _protection.Evaluate(target, grant, match?.UserOverride is { Allowed: false });
        return new(match, SideButtonPolicy, status, grant, _profiles.Resolve(match?.AppId, status));
    }

    private void ShowForeground()
    {
        if (_closed)
            return;
        if (LastForeground is not { } running)
        {
            ForegroundTitle.Text = "当前焦点应用";
            _shell.UpdateForeground("等待可识别的其他前台应用", false);
            ForegroundProfileText.Text = "当前生效方案 · 无";
            UpdateDockState("等待前台应用", null, ProtectionStatus.Unmatched);
            return;
        }

        var current = ForegroundApplicationService.IsCurrent(running);
        ForegroundTitle.Text = current ? "当前焦点应用" : "最近的其他前台应用";
        if (running.WindowKind == ForegroundWindowKind.ExplorerDesktop)
        {
            var desktopStatus = _protection.IsPaused ? ProtectionStatus.Paused : ProtectionStatus.Ready;
            var ready = running.InstallationStamp is not null
                && WindowsProcessQuery.CanSendInputTo((uint)running.WindowOwnerProcessId);
            var runningOnDesktop = ready && _desktopInput.IsRunning && !_desktopInput.IsTestMode;
            var activeOnDesktop = runningOnDesktop && !_protection.IsPaused && current;
            ForegroundProfileText.Text = activeOnDesktop
                ? $"当前生效方案 · {GlobalProfile.Name} · {GlobalProfile.SectorCount} 方向"
                : $"待用方案 · {GlobalProfile.Name}";
            _shell.UpdateForeground($"Windows 桌面 · {ProtectionLabels.Trigger(_settings.SideButton)}", false);
            UpdateDockState("Windows 桌面", null, desktopStatus, inputRestricted: !ready);
            return;
        }

        var (match, _, status, granted, profile) = ResolveForeground(running);
        var name = match?.DisplayName ?? running.ExecutableName ?? running.Aumid ?? "应用身份不明";
        var kind = running.WindowKind switch
        {
            ForegroundWindowKind.ExplorerFolder => "文件资源管理器窗口",
            ForegroundWindowKind.PackagedApp => "封装应用",
            ForegroundWindowKind.ApplicationHost => "宿主身份待确认",
            _ => "桌面程序"
        };
        var trigger = granted is null ? "未启用侧键笔势" : ProtectionLabels.Trigger(_settings.SideButton);
        var canSend = granted is not null && WindowsProcessQuery.CanSendInputTo((uint)running.WindowOwnerProcessId);
        var measures = granted is null ? "侧键保持原生行为"
            : canSend ? "已核对前台窗口"
            : "目标完整性级别过高或无法核对，原生输入保持放行";
        var active = profile is not null && canSend && _desktopInput.IsRunning && _globalInputEnabled && current;
        ForegroundProfileText.Text = active ? $"当前生效方案 · {profile!.Name} · {profile.SectorCount} 方向"
            : profile is null ? "当前生效方案 · 无" : $"待用方案 · {profile.Name} · {profile.SectorCount} 方向";
        _shell.UpdateForeground($"{name} · {kind} · {ProtectionLabels.Status(status)}\n{trigger} · {measures}",
            match is not null && IsListedApplication(match));
        UpdateDockState(name, match, status, inputRestricted: granted is not null && !canSend);
    }

    private void ForegroundSettingsClick(object sender, RoutedEventArgs e)
    {
        if (LastForeground is not { } running
            || ForegroundApplicationService.Match(running, _rows.Select(row => row.Application)) is not { } app || !IsListedApplication(app))
            return;

        _shell.NavigateTo(DesktopPage.Applications);
        SearchBox.Clear();
        EnabledFilter.SelectedIndex = 0;
        ApplicationList.SelectedItem = _rows.First(row => row.Application.AppId == app.AppId);
        ApplicationList.ScrollIntoView(ApplicationList.SelectedItem);
    }


    private static string SourceText(DiscoverySource sources)
    {
        var labels = new List<string>();
        if (sources.HasFlag(DiscoverySource.Desktop)) labels.Add("桌面");
        if (sources.HasFlag(DiscoverySource.Office)) labels.Add("Microsoft Office");
        if (sources.HasFlag(DiscoverySource.UserRegistry32)) labels.Add("用户注册表 32 位");
        if (sources.HasFlag(DiscoverySource.UserRegistry64)) labels.Add("用户注册表 64 位");
        if (sources.HasFlag(DiscoverySource.SystemRegistry32)) labels.Add("系统注册表 32 位");
        if (sources.HasFlag(DiscoverySource.SystemRegistry64)) labels.Add("系统注册表 64 位");
        if (sources.HasFlag(DiscoverySource.StartMenu)) labels.Add("开始菜单");
        if (sources.HasFlag(DiscoverySource.Appx)) labels.Add("MSIX/Appx");
        if (sources.HasFlag(DiscoverySource.Running)) labels.Add("运行进程");
        if (sources.HasFlag(DiscoverySource.Manual)) labels.Add("手动添加");
        return string.Join("、", labels);
    }

    private static bool IsReadError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or SecurityException or Win32Exception or ExternalException;
}
