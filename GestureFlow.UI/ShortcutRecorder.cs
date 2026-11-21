using System.Windows.Input;
using GestureFlow.Core;

namespace GestureFlow.UI;

public static class ShortcutRecorder
{
    public static KeyboardShortcut? ParseSequence(string text)
    {
        var steps = text.Split([',', '，'], StringSplitOptions.TrimEntries);
        if (steps.Length is < 2 or > 16)
            return null;

        var shortcuts = new List<KeyboardShortcut>(steps.Length);
        foreach (var step in steps)
        {
            if (Parse(step) is not { } chord)
                return null;
            shortcuts.Add(chord);
        }
        return shortcuts[0] with { FollowingSteps = [.. shortcuts.Skip(1)] };
    }

    public static KeyboardShortcut? Parse(string text)
    {
        var modifiers = ShortcutModifiers.None;
        var keys = new List<ShortcutKey>();
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var flag = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ShortcutModifiers.Control,
                "ALT" => ShortcutModifiers.Alt,
                "SHIFT" => ShortcutModifiers.Shift,
                "WIN" or "WINDOWS" => ShortcutModifiers.Windows,
                _ => ShortcutModifiers.None
            };
            if (flag != ShortcutModifiers.None)
            {
                if ((modifiers & flag) != 0)
                    return null;
                modifiers |= flag;
                continue;
            }
            var name = part.ToUpperInvariant() switch
            {
                "ESC" => "Escape",
                "DEL" => "Delete",
                _ when part.Length == 1 && char.IsAsciiDigit(part[0]) => "Digit" + part,
                _ => part
            };
            if (!Enum.TryParse<ShortcutKey>(name, true, out var key)
                || !string.Equals(key.ToString(), name, StringComparison.OrdinalIgnoreCase) || keys.Contains(key))
                return null;
            keys.Add(key);
        }
        return keys.Count == 0 ? null : new(keys[0], modifiers, [.. keys.Skip(1)]);
    }

    public static Key GetKey(KeyEventArgs input) => input.Key switch
    {
        Key.System => input.SystemKey,
        Key.ImeProcessed => input.ImeProcessedKey,
        _ => input.Key
    };

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    public static KeyboardShortcut? Capture(Key key, ModifierKeys modifiers)
    {
        const ModifierKeys allowed = ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Windows;
        if (!Enum.IsDefined(key) || IsModifier(key) || (modifiers & ~allowed) != 0)
            return null;

        var mapped = key switch
        {
            Key.D0 or Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 or Key.D6 or Key.D7 or Key.D8 or Key.D9 =>
                ShortcutKey.Digit0 + (key - Key.D0),
            Key.Escape => ShortcutKey.Escape,
            Key.Return => ShortcutKey.Enter,
            Key.Capital => ShortcutKey.CapsLock,
            Key.Snapshot => ShortcutKey.PrintScreen,
            Key.Prior => ShortcutKey.PageUp,
            Key.Next => ShortcutKey.PageDown,
            Key.Back => ShortcutKey.Backspace,
            Key.Scroll => ShortcutKey.ScrollLock,
            Key.Apps => ShortcutKey.Applications,
            Key.Add => ShortcutKey.NumPadAdd,
            Key.Subtract => ShortcutKey.NumPadSubtract,
            Key.Multiply => ShortcutKey.NumPadMultiply,
            Key.Divide => ShortcutKey.NumPadDivide,
            Key.Decimal => ShortcutKey.NumPadDecimal,
            Key.Oem1 => ShortcutKey.OemSemicolon,
            Key.Oem2 => ShortcutKey.OemSlash,
            Key.Oem3 => ShortcutKey.OemTilde,
            Key.Oem4 => ShortcutKey.OemOpenBrackets,
            Key.Oem5 => ShortcutKey.OemBackslash,
            Key.Oem6 => ShortcutKey.OemCloseBrackets,
            Key.Oem7 => ShortcutKey.OemQuote,
            Key.Oem102 => ShortcutKey.Oem102,
            _ => Enum.TryParse<ShortcutKey>(key.ToString(), out var sameName) ? sameName : default
        };

        var flags = ShortcutModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= ShortcutModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= ShortcutModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= ShortcutModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Windows)) flags |= ShortcutModifiers.Windows;

        var shortcut = new KeyboardShortcut(mapped, flags);
        return shortcut.IsValid() ? shortcut : null;
    }
}

public sealed class ShortcutChordRecording
{
    private readonly List<ShortcutKey> _keys = [];
    private readonly HashSet<Key> _held = [];
    private ShortcutModifiers _modifiers;

    public KeyboardShortcut? Preview =>
        _keys.Count == 0 ? null : new(_keys[0], _modifiers, _keys.Count == 1 ? default : [.. _keys.Skip(1)]);

    public bool Press(Key key, ModifierKeys modifiers)
    {
        if (ShortcutRecorder.Capture(key, modifiers) is not { } shortcut)
            return false;

        if (_held.Add(key))
        {
            if (!_keys.Contains(shortcut.Key))
                _keys.Add(shortcut.Key);
            _modifiers |= shortcut.Modifiers;
        }
        return true;
    }

    public KeyboardShortcut? Release(Key key) => _held.Remove(key) && _held.Count == 0 ? Preview : null;
}
