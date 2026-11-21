using System.ComponentModel;
using GestureFlow.Core;
using GestureFlow.Profiles;
using GestureFlow.UI;

namespace GestureFlow.Desktop;

internal sealed class GestureConfigurationViewModel : INotifyPropertyChanged
{
    private readonly GestureProfileEditor _editor;
    private GestureProfile _saved;
    private string? _feedback;

    public GestureConfigurationViewModel(GestureProfile profile)
    {
        _editor = new(profile);
        _saved = _editor.Profile;
        RebuildRows();
    }

    public IReadOnlyList<GestureBindingDraft> Rows { get; private set; } = [];
    public GestureProfile Profile => _editor.Profile;
    public bool NamesValid => Rows.All(row => !string.IsNullOrWhiteSpace(row.DisplayName));
    public bool HasChanges => !NamesValid || Profile.Mode != _saved.Mode || !Profile.Bindings.SequenceEqual(_saved.Bindings);
    public bool CanSave => NamesValid;

    public GestureMode Mode
    {
        get => Profile.Mode;
        set
        {
            if (value == Mode || !Enum.IsDefined(value) || !NamesValid)
                return;

            _editor.ChangeMode(value);
            Feedback = null;
            RebuildRows();
        }
    }

    public string State => !NamesValid ? "显示名称不能为空。轮盘保留上一个有效名称。"
        : HasChanges ? "有未保存的更改。" : "显示已保存的方向绑定。";

    public string? Feedback
    {
        get => _feedback;
        set
        {
            if (_feedback == value)
                return;

            _feedback = value;
            PropertyChanged?.Invoke(this, new(nameof(Feedback)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Load(GestureProfile profile)
    {
        _editor.Load(profile);
        _saved = Profile;
        Feedback = null;
        RebuildRows();
    }

    public void RestoreDefaults()
    {
        _editor.Load(DefaultGestureProfiles.Create(Mode) with { Applications = Profile.Applications });
        Feedback = "已载入默认动作和快捷键。";
        RebuildRows();
    }

    public void MarkSaved()
    {
        _saved = Profile;
        Feedback = "已保存手势配置。";
        NotifyState();
    }

    public bool SetShortcut(GestureBindingDraft row, KeyboardShortcut shortcut)
    {
        if (!_editor.SetShortcut(row.Direction, shortcut))
            return false;

        row.SetShortcut(shortcut);
        NotifyState();
        return true;
    }

    private void EditName(GestureBindingDraft row)
    {
        _editor.SetName(row.Direction, row.DisplayName);
        Feedback = null;
        NotifyState();
    }

    private void RebuildRows()
    {
        Rows = Profile.Bindings.Select(binding => new GestureBindingDraft(binding, EditName)).ToArray();
        NotifyState();
    }

    private void NotifyState() => PropertyChanged?.Invoke(this, new(null));
}

internal sealed class GestureBindingDraft(GestureBinding binding, Action<GestureBindingDraft> nameChanged) : INotifyPropertyChanged
{
    private string _displayName = binding.DisplayName;
    private KeyboardShortcut _shortcut = binding.Shortcut;
    private bool _recording;

    public GestureDirection Direction => binding.Direction;
    public string DirectionLabel => GestureLabels.Direction(Direction);
    public string Availability => binding.Enabled ? "" : "已停用";
    public string NameFieldLabel => $"{DirectionLabel}的显示名称";
    public string ShortcutFieldLabel => $"录制{DirectionLabel}的快捷键";
    public string ShortcutLabel => _recording ? "按下组合键…" : GestureLabels.Shortcut(_shortcut);

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value)
                return;

            _displayName = value;
            PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
            nameChanged(this);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetRecording(bool recording)
    {
        _recording = recording;
        PropertyChanged?.Invoke(this, new(nameof(ShortcutLabel)));
    }

    public void SetShortcut(KeyboardShortcut shortcut)
    {
        _shortcut = shortcut;
        PropertyChanged?.Invoke(this, new(nameof(ShortcutLabel)));
    }
}
