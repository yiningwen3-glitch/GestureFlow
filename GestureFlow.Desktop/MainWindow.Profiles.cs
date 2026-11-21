using System.IO;
using System.Windows;
using System.Windows.Controls;
using GestureFlow.Desktop.Applications;
using GestureFlow.Profiles;
using Microsoft.Win32;

namespace GestureFlow.Desktop;

public partial class MainWindow
{
    private sealed record ProfileScopeChoice(string? AppId, string Name);

    private GestureProfileManager _profiles = new();
    private string? _profileAppId;
    private Guid? _editingProfileId;
    private bool _syncingProfiles;
    private bool _editingActiveProfile;

    private ManagedGestureProfile GlobalProfile => _profiles.Selected() ?? GestureProfileManager.SystemDefault;
    private ManagedGestureProfile? BrowsedProfile => ProfilePicker.SelectedItem as ManagedGestureProfile;

    private void RefreshProfileScopes()
    {
        _syncingProfiles = true;
        try
        {
            var choices = new List<ProfileScopeChoice> { new(null, "Windows 桌面（通用方案）") };
            choices.AddRange(_rows.Select(row => row.Application)
                .Where(app => IsListedApplication(app) && app.IdentificationStatus == IdentificationStatus.Matched)
                .Select(app => new ProfileScopeChoice(app.AppId, app.DisplayName)));
            var selected = choices.FirstOrDefault(choice => string.Equals(choice.AppId, _profileAppId, StringComparison.OrdinalIgnoreCase));
            if (selected is null && GestureConfiguration.Model.HasChanges
                && ProfileScopePicker.SelectedItem is ProfileScopeChoice editedScope)
            {
                choices.Add(editedScope);
                selected = editedScope;
            }
            selected ??= choices[0];
            _profileAppId = selected.AppId;
            ProfileScopePicker.ItemsSource = choices;
            ProfileScopePicker.SelectedItem = selected;
        }
        finally { _syncingProfiles = false; }
    }

    private void ShowProfiles(Guid? preferred = null)
    {
        _syncingProfiles = true;
        try
        {
            var items = _profiles.Profiles.Where(profile =>
                string.Equals(profile.AppId, _profileAppId, StringComparison.OrdinalIgnoreCase)).ToArray();
            var selected = _profiles.Selected(_profileAppId);
            ProfilePicker.ItemsSource = items;
            ProfilePicker.SelectedItem = items.FirstOrDefault(profile => profile.ProfileId == (preferred ?? selected?.ProfileId))
                ?? items.FirstOrDefault();
            UpdateSelectedProfileText();
            PreviewBrowsedProfile();
        }
        finally { _syncingProfiles = false; }
    }

    private void UpdateSelectedProfileText()
    {
        var selected = _profiles.Selected(_profileAppId);
        var scope = (ProfileScopePicker.SelectedItem as ProfileScopeChoice)?.Name ?? "Windows 桌面";
        EditingScopeText.Text = $"正在编辑 {scope} 的笔势";
        SelectedProfileText.Text = _profileAppId is not null && selected is null
            ? $"当前选用 · 跟随通用方案（{GlobalProfile.Name}）"
            : $"当前选用 · {(selected ?? GlobalProfile).Name}";
    }

    private void PreviewBrowsedProfile()
    {
        var profile = BrowsedProfile;
        _editingProfileId = null;
        var active = _profiles.Selected(_profileAppId);
        _editingActiveProfile = active?.ProfileId == profile?.ProfileId;
        if (_editingActiveProfile && profile is { ReadOnly: false })
            _editingProfileId = profile.ProfileId;
        ProfileNameBox.Text = profile?.Name ?? "";
        GestureConfiguration.SetProfile((profile ?? GlobalProfile).Configuration);
        GestureConfiguration.SetEditing(_editingActiveProfile);
        EditProfileButton.IsEnabled = RenameProfileButton.IsEnabled = DeleteProfileButton.IsEnabled =
            profile is { ReadOnly: false };
        SelectProfileButton.IsEnabled = ExportProfileButton.IsEnabled = CopyProfileButton.IsEnabled = profile is not null;
    }

    private bool DiscardProfileDraft() => !GestureConfiguration.Model.HasChanges
        || MessageBox.Show(this, "要放弃此方案未保存的修改吗？", "切换方案",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void ProfileScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProfiles || ProfileScopePicker.SelectedItem is not ProfileScopeChoice choice)
            return;
        if (!DiscardProfileDraft())
        {
            RefreshProfileScopes();
            return;
        }
        _profileAppId = choice.AppId;
        ShowProfiles();
    }

    private void DesktopProfilesClick(object sender, RoutedEventArgs e)
    {
        if (!DiscardProfileDraft())
            return;
        _profileAppId = null;
        RefreshProfileScopes();
        ShowProfiles();
        _shell.NavigateTo(DesktopPage.Gestures);
    }

    private void AppProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ApplicationRow row })
            OpenAppProfile(row.Application);
        else if (SelectedApplication is { } app)
            OpenAppProfile(app);
    }

    private void OpenAppProfile(InstalledApplication app)
    {
        if (app.IdentificationStatus != IdentificationStatus.Matched || !DiscardProfileDraft())
            return;
        _profileAppId = app.AppId;
        RefreshProfileScopes();
        ShowProfiles();
        _shell.NavigateTo(DesktopPage.Gestures);
    }

    private void ForegroundProfilesClick(object sender, RoutedEventArgs e)
    {
        if (LastForeground is not { } running || ResolveForeground(running).Application is not { } app
            || !IsListedApplication(app) || app.IdentificationStatus != IdentificationStatus.Matched)
            return;
        if (!string.Equals(_profileAppId, app.AppId, StringComparison.OrdinalIgnoreCase))
        {
            if (!DiscardProfileDraft())
                return;
            _profileAppId = app.AppId;
            RefreshProfileScopes();
            ShowProfiles();
        }
        _shell.NavigateTo(DesktopPage.Gestures);
    }

    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProfiles)
            return;
        if (!DiscardProfileDraft())
        {
            _syncingProfiles = true;
            ProfilePicker.SelectedItem = e.RemovedItems.Count == 0 ? null : e.RemovedItems[0];
            _syncingProfiles = false;
            return;
        }
        PreviewBrowsedProfile();
    }

    private void EditProfileClick(object sender, RoutedEventArgs e)
    {
        if (BrowsedProfile is not { ReadOnly: false } profile)
            return;
        _editingProfileId = profile.ProfileId;
        _editingActiveProfile = _profiles.Selected(_profileAppId)?.ProfileId == profile.ProfileId;
        GestureConfiguration.SetEditing(true);
        GestureConfiguration.BeginEditing();
    }

    private void CreateProfileClick(object sender, RoutedEventArgs e)
    {
        if (DiscardProfileDraft())
            ChangeProfiles(() =>
            {
                var name = ProfileNameBox.Text.Trim();
                if (name.Length == 0 || name == BrowsedProfile?.Name)
                    name = "新方案";
                ShowProfiles(_profiles.Create(name, _profileAppId).ProfileId);
                EditProfileClick(sender, e);
            }, "已新建方案。");
    }

    private void CopyProfileClick(object sender, RoutedEventArgs e)
    {
        if (BrowsedProfile is { } profile && DiscardProfileDraft())
            ChangeProfiles(() =>
            {
                var name = ProfileNameBox.Text.Trim();
                if (name.Length == 0 || name == profile.Name)
                    name = $"{profile.Name}（副本）";
                ShowProfiles(_profiles.Copy(profile.ProfileId, name, _profileAppId).ProfileId);
                EditProfileClick(sender, e);
            }, "已复制方案。");
    }

    private void RenameProfileClick(object sender, RoutedEventArgs e)
    {
        if (BrowsedProfile is { ReadOnly: false } profile)
            ChangeProfiles(() =>
            {
                ReplaceBrowsedProfile(_profiles.Rename(profile.ProfileId, ProfileNameBox.Text));
            }, "已重命名方案。");
    }

    private void DeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (BrowsedProfile is { ReadOnly: false } profile && MessageBox.Show(this,
            $"删除「{profile.Name}」及其未保存的修改？", "删除方案",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            ChangeProfiles(() =>
            {
                _profiles.Delete(profile.ProfileId);
                ShowProfiles();
            }, "已删除方案。");
    }

    private void SelectProfileClick(object sender, RoutedEventArgs e)
    {
        if (BrowsedProfile is { } profile)
            ChangeProfiles(() => _profiles.Select(profile.ProfileId, _profileAppId), "已选用此方案。");
    }

    private void ResetProfileSelectionClick(object sender, RoutedEventArgs e) =>
        ChangeProfiles(() => _profiles.Select(null, _profileAppId),
            _profileAppId is null ? "已选用系统默认方案。" : "此应用已改为跟随通用方案。");

    private void SaveManagedProfileClick(object sender, RoutedEventArgs e)
    {
        if (!GestureConfiguration.Model.CanSave || (!_editingActiveProfile && _editingProfileId is null))
            return;
        ChangeProfiles(() =>
        {
            if (_editingProfileId is { } id)
                ReplaceBrowsedProfile(_profiles.Edit(id, GestureConfiguration.Model.Profile));
            else if (GestureConfiguration.Model.HasChanges)
            {
                var scope = (ProfileScopePicker.SelectedItem as ProfileScopeChoice)?.Name ?? "Windows 桌面";
                var name = _profileAppId is null ? "我的桌面笔势" : $"{scope} 专属方案";
                var profile = _profiles.Create(name, _profileAppId, GestureConfiguration.Model.Profile, select: true);
                ShowProfiles(profile.ProfileId);
            }
            GestureConfiguration.Model.MarkSaved();
        }, _editingActiveProfile ? "已保存并应用笔势。" : "已保存方向绑定。");
    }

    private void ReplaceBrowsedProfile(ManagedGestureProfile profile)
    {
        var items = (ManagedGestureProfile[])ProfilePicker.ItemsSource;
        _syncingProfiles = true;
        try
        {
            ProfilePicker.ItemsSource = items.Select(item => item.ProfileId == profile.ProfileId ? profile : item).ToArray();
            ProfilePicker.SelectedItem = profile;
            ProfileNameBox.Text = profile.Name;
        }
        finally { _syncingProfiles = false; }
    }

    private void ImportProfileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入方案", Filter = "GestureFlow JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true && DiscardProfileDraft())
            ChangeProfiles(() => ShowProfiles(_profiles.Import(File.ReadAllText(dialog.FileName), _profileAppId).ProfileId),
                "已导入方案，可在列表中选用。");
    }

    private void ExportProfileClick(object sender, RoutedEventArgs e)
    {
        if (BrowsedProfile is not { } profile)
            return;
        var dialog = new SaveFileDialog { Title = "导出已保存的方案", FileName = "GestureFlow-profile.json",
            Filter = "GestureFlow JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            _settingsStore.ValidateExportLocation(dialog.FileName);
            GestureProfileManager.AtomicWrite(dialog.FileName, _profiles.Export(profile.ProfileId));
            ProfileStatus.Text = "已导出方案。";
        }
        catch (Exception error) when (IsReadError(error))
        {
            ProfileStatus.Text = $"导出失败 · {error.Message}";
        }
    }

    private void ChangeProfiles(Action change, string feedback)
    {
        try
        {
            change();
            UpdateSelectedProfileText();
            ApplyGestureSettings();
            ShowRows(_rows, SelectedApplication?.AppId);
            RefreshApplicationDetails();
            ShowForeground();
            ProfileStatus.Text = feedback;
        }
        catch (Exception error) when (IsReadError(error) || error is ProfileValidationException or InvalidOperationException)
        {
            ProfileStatus.Text = $"操作失败 · {error.Message}";
        }
    }
}
