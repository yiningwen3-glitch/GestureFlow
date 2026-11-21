using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace GestureFlow.Core;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8
}

public enum ShortcutKey
{
    A = 1, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Digit0, Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
    Left, Up, Right, Down, Home, End, PageUp, PageDown,
    Insert, Delete, Backspace, Tab, Enter, Escape, Space,
    PrintScreen, ScrollLock, Pause, CapsLock, NumLock,
    NumPad0, NumPad1, NumPad2, NumPad3, NumPad4,
    NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    NumPadAdd, NumPadSubtract, NumPadMultiply, NumPadDivide, NumPadDecimal,
    OemMinus, OemPlus, OemOpenBrackets, OemCloseBrackets,
    OemBackslash, OemSemicolon, OemQuote, OemComma, OemPeriod, OemSlash, OemTilde,
    Oem102, Applications
}

public sealed record KeyboardShortcut(ShortcutKey Key, ShortcutModifiers Modifiers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<ShortcutKey> AdditionalKeys = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<KeyboardShortcut> FollowingSteps = default)
{
    [JsonIgnore]
    public bool IsSequence => !FollowingSteps.IsDefaultOrEmpty;

    public IEnumerable<ShortcutKey> GetKeys()
    {
        yield return Key;
        if (!AdditionalKeys.IsDefault)
            foreach (var key in AdditionalKeys)
                yield return key;
    }

    public bool IsValid()
    {
        const ShortcutModifiers allowed =
            ShortcutModifiers.Control | ShortcutModifiers.Shift | ShortcutModifiers.Alt | ShortcutModifiers.Windows;
        var seen = new HashSet<ShortcutKey>();
        return (Modifiers & ~allowed) == 0 && GetKeys().All(key => Enum.IsDefined(key) && seen.Add(key))
            && (!IsSequence || FollowingSteps.Length <= 15
                && FollowingSteps.All(step => step is { IsSequence: false } && step.IsValid()));
    }

    public bool Equals(KeyboardShortcut? other) =>
        other is not null && Modifiers == other.Modifiers && GetKeys().SequenceEqual(other.GetKeys())
        && (IsSequence ? other.IsSequence && FollowingSteps.SequenceEqual(other.FollowingSteps) : !other.IsSequence);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Modifiers);
        foreach (var key in GetKeys())
            hash.Add(key);
        if (IsSequence)
            foreach (var step in FollowingSteps)
                hash.Add(step);
        return hash.ToHashCode();
    }
}
