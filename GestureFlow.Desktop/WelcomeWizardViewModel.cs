using System.ComponentModel;

namespace GestureFlow.Desktop;

internal sealed class WelcomeWizardViewModel : INotifyPropertyChanged
{
    internal sealed record Step(string Title, string Body, bool OffersPractice = false, bool OffersSideButton = false);

    private static readonly Step[] Steps =
    [
        new("选一个鼠标侧键", "按住所选侧键，拖向目标方向，再松开执行。", OffersSideButton: true),
        new("试一次", "在下面的练习区按住所选侧键并拖动。选中的方向会变灰，松开侧键即可确认。", OffersPractice: true),
        new("开始使用", "可以先在 Windows 桌面使用笔势。要在 Word 等软件内使用，打开应用页并启用该应用。")
    ];

    private int _index;
    public Step Current => Steps[_index];
    public string Progress => $"第 {_index + 1} 步，共 {Steps.Length} 步";
    public bool CanGoBack => _index > 0;
    public bool HasNext => _index < Steps.Length - 1;
    public bool IsLast => !HasNext;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Move(int delta)
    {
        _index = Math.Clamp(_index + delta, 0, Steps.Length - 1);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
