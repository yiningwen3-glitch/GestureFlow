using System.Runtime.InteropServices;
using GestureFlow.Core;

namespace GestureFlow.Desktop.Input;

internal enum ActionResult { Sent, InvalidShortcut, UnsafeTarget, ConflictingKeyboardState, NativeFailure }

internal sealed class WindowsActionExecutor
{
    private static readonly (ShortcutModifiers Modifier, ushort Key)[] ModifierKeys =
    [
        (ShortcutModifiers.Control, 0xA2), (ShortcutModifiers.Alt, 0xA4),
        (ShortcutModifiers.Shift, 0xA0), (ShortcutModifiers.Windows, 0x5B)
    ];

    private readonly Func<ShortcutModifiers> _heldModifiers;
    private readonly Func<int, bool> _isDown;
    private readonly Func<WindowsInputNative.Input[], uint> _send;

    public WindowsActionExecutor() : this(WindowsInputNative.HeldModifiers, WindowsInputNative.IsDown, SendNative) { }

    internal WindowsActionExecutor(Func<ShortcutModifiers> heldModifiers, Func<int, bool> isDown,
        Func<WindowsInputNative.Input[], uint> send)
    {
        _heldModifiers = heldModifiers;
        _isDown = isDown;
        _send = send;
    }

    public ActionResult TryExecute(KeyboardShortcut shortcut, Func<bool> stillSafe)
    {
        if (shortcut.IsSequence || !shortcut.IsValid())
            return ActionResult.InvalidShortcut;
        var keys = shortcut.GetKeys().Select(VirtualKey).ToArray();
        if (keys.Contains((ushort)0))
            return ActionResult.InvalidShortcut;
        if (!stillSafe())
            return ActionResult.UnsafeTarget;

        var held = _heldModifiers();
        if ((held & ~shortcut.Modifiers) != 0 || keys.Any(key => _isDown(key)))
            return ActionResult.ConflictingKeyboardState;

        var newlyPressed = ModifierKeys.Where(entry => (shortcut.Modifiers & entry.Modifier) != 0 && (held & entry.Modifier) == 0)
            .Select(entry => entry.Key).Concat(keys).ToArray();
        var batch = newlyPressed.Select(key => KeyInput(key, up: false))
            .Concat(newlyPressed.Reverse().Select(key => KeyInput(key, up: true))).ToArray();

        if (_heldModifiers() != held || keys.Any(key => _isDown(key)))
            return ActionResult.ConflictingKeyboardState;
        if (!stillSafe())
            return ActionResult.UnsafeTarget;
        var sent = _send(batch);
        if (sent == batch.Length)
            return ActionResult.Sent;

        // Release only keys whose own down reached Windows and whose corresponding up did not.
        var stillDown = Math.Min((int)sent, batch.Length - (int)sent);
        if (stillDown > 0)
            _send(newlyPressed.Take(stillDown).Reverse().Select(key => KeyInput(key, up: true)).ToArray());

        return ActionResult.NativeFailure;
    }


    internal static ushort VirtualKey(ShortcutKey key) => key switch
    {
        >= ShortcutKey.A and <= ShortcutKey.Z => (ushort)(0x41 + (key - ShortcutKey.A)),
        >= ShortcutKey.Digit0 and <= ShortcutKey.Digit9 => (ushort)(0x30 + (key - ShortcutKey.Digit0)),
        >= ShortcutKey.F1 and <= ShortcutKey.F24 => (ushort)(0x70 + (key - ShortcutKey.F1)),
        >= ShortcutKey.NumPad0 and <= ShortcutKey.NumPad9 => (ushort)(0x60 + (key - ShortcutKey.NumPad0)),
        ShortcutKey.Left => 0x25, ShortcutKey.Up => 0x26, ShortcutKey.Right => 0x27, ShortcutKey.Down => 0x28,
        ShortcutKey.Home => 0x24, ShortcutKey.End => 0x23, ShortcutKey.PageUp => 0x21, ShortcutKey.PageDown => 0x22,
        ShortcutKey.Insert => 0x2D, ShortcutKey.Delete => 0x2E, ShortcutKey.Backspace => 0x08, ShortcutKey.Tab => 0x09,
        ShortcutKey.Enter => 0x0D, ShortcutKey.Escape => 0x1B, ShortcutKey.Space => 0x20, ShortcutKey.PrintScreen => 0x2C,
        ShortcutKey.ScrollLock => 0x91, ShortcutKey.Pause => 0x13, ShortcutKey.CapsLock => 0x14, ShortcutKey.NumLock => 0x90,
        ShortcutKey.NumPadAdd => 0x6B, ShortcutKey.NumPadSubtract => 0x6D, ShortcutKey.NumPadMultiply => 0x6A,
        ShortcutKey.NumPadDivide => 0x6F, ShortcutKey.NumPadDecimal => 0x6E, ShortcutKey.OemMinus => 0xBD,
        ShortcutKey.OemPlus => 0xBB, ShortcutKey.OemOpenBrackets => 0xDB, ShortcutKey.OemCloseBrackets => 0xDD,
        ShortcutKey.OemBackslash => 0xDC, ShortcutKey.OemSemicolon => 0xBA, ShortcutKey.OemQuote => 0xDE,
        ShortcutKey.OemComma => 0xBC, ShortcutKey.OemPeriod => 0xBE, ShortcutKey.OemSlash => 0xBF,
        ShortcutKey.OemTilde => 0xC0, ShortcutKey.Oem102 => 0xE2, ShortcutKey.Applications => 0x5D,
        _ => 0
    };

    private static WindowsInputNative.Input KeyInput(ushort key, bool up)
    {
        var extended = key is >= 0x21 and <= 0x28 or 0x2C or 0x2D or 0x2E or 0x5B or 0x5D or 0x6F or 0x90;
        return new()
        {
            Type = 1,
            Data = new()
            {
                Keyboard = new()
                {
                    Key = key, Flags = (extended ? 1u : 0u) | (up ? 2u : 0u), ExtraInfo = WindowsInputNative.InjectionMarker
                }
            }
        };
    }


    private static uint SendNative(WindowsInputNative.Input[] inputs) =>
        WindowsInputNative.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<WindowsInputNative.Input>());
}
