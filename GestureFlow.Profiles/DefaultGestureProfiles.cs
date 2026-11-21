using GestureFlow.Core;

namespace GestureFlow.Profiles;

public static class DefaultGestureProfiles
{
    public static GestureProfile Create(GestureMode mode)
    {
        var bindings = DirectionRecognizer.GetDirections(mode).Select(direction => direction switch
        {
            GestureDirection.North => Bind(direction, "复制", ShortcutKey.C),
            GestureDirection.NorthEast => Bind(direction, "重做", ShortcutKey.Y),
            GestureDirection.East => Bind(direction, "粘贴", ShortcutKey.V),
            GestureDirection.SouthEast => Bind(direction, "全选", ShortcutKey.A),
            GestureDirection.South => Bind(direction, "撤销", ShortcutKey.Z),
            GestureDirection.SouthWest => Bind(direction, "查找", ShortcutKey.F),
            GestureDirection.West => Bind(direction, "剪切", ShortcutKey.X),
            GestureDirection.NorthWest => Bind(direction, "保存", ShortcutKey.S),
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        });

        return new GestureProfile(mode, Array.AsReadOnly(bindings.ToArray()), Array.Empty<ApplicationRule>());
    }

    private static GestureBinding Bind(GestureDirection direction, string name, ShortcutKey key) =>
        new(direction, name, new KeyboardShortcut(key, ShortcutModifiers.Control), true);
}
