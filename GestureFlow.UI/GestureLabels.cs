using GestureFlow.Core;

namespace GestureFlow.UI;

public static class GestureLabels
{
    public static string Direction(GestureDirection direction) => direction switch
    {
        GestureDirection.North => "向上",
        GestureDirection.NorthEast => "右上",
        GestureDirection.East => "向右",
        GestureDirection.SouthEast => "右下",
        GestureDirection.South => "向下",
        GestureDirection.SouthWest => "左下",
        GestureDirection.West => "向左",
        GestureDirection.NorthWest => "左上",
        _ => direction.ToString()
    };

    public static string Shortcut(KeyboardShortcut shortcut) => shortcut.IsSequence
        ? string.Join(", ", new[] { Chord(shortcut) }.Concat(shortcut.FollowingSteps.Select(Chord)))
        : Chord(shortcut);

    private static string Chord(KeyboardShortcut shortcut)
    {
        var parts = new List<string>();
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Windows)) parts.Add("Win");
        foreach (var key in shortcut.GetKeys())
        {
            var label = key.ToString();
            parts.Add(label.StartsWith("Digit", StringComparison.Ordinal) ? label[5..] : label);
        }
        return string.Join("+", parts);
    }
}
