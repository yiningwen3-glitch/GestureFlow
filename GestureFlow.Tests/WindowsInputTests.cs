using GestureFlow.Core;
using GestureFlow.Core.Protection;
using GestureFlow.Desktop.Input;
using GestureFlow.Lab.Simulation;
using GestureFlow.Profiles;

namespace GestureFlow.Tests;

public sealed class WindowsInputTests
{
    [Theory]
    [InlineData(59, 1.5)]
    [InlineData(71, 1.0)]
    [InlineData(83, 2.0)]
    public void SessionAppliesConfiguredDistanceAfterDpiConversion(int threshold, double scale)
    {
        var origin = new GesturePoint(-300, 420);
        var session = new ScreenGestureSession(DefaultGestureProfiles.Create(GestureMode.Four), origin, scale, threshold);
        session.Process(new(GestureInputKind.Press, origin));

        var inside = new GesturePoint(origin.X + (threshold - 1) * scale, origin.Y);
        Assert.Null(session.Process(new(GestureInputKind.Move, inside)).Update.Candidate);
        var edge = new GesturePoint(origin.X + threshold * scale, origin.Y);
        Assert.Equal(new(GestureState.Tracking, GestureDirection.East, null), session.Process(new(GestureInputKind.Move, edge)).Update);
        Assert.Equal(GestureDirection.East, session.Process(new(GestureInputKind.Release, edge)).Update.CommittedDirection);
    }

    [Theory]
    [InlineData(GestureMode.Four, 1.0)]
    [InlineData(GestureMode.Four, 1.5)]
    [InlineData(GestureMode.Eight, 1.0)]
    [InlineData(GestureMode.Eight, 2.0)]
    public void NativeAdapterAndLabAgreeOnDirectionsAndBindings(GestureMode mode, double scale)
    {
        var profile = DefaultGestureProfiles.Create(mode);
        var origin = new GesturePoint(-570, 420);
        foreach (var direction in DirectionRecognizer.GetDirections(GestureMode.Eight))
        {
            var events = GestureScenarios.Direction(direction);
            var lab = new GestureLabSession(profile);
            var native = new ScreenGestureSession(profile, origin, scale);
            var permission = Permission(profile, GestureTrigger.MouseX1);
            var routed = new List<CapturedInput>();
            var router = new MouseGestureRouter(routed.Add);
            foreach (var input in events)
            {
                var signal = input.Kind switch
                {
                    GestureInputKind.Press => MouseSignal.Down,
                    GestureInputKind.Release => MouseSignal.Up,
                    _ => MouseSignal.Move
                };
                var packet = new MousePacket(signal, signal == MouseSignal.Move ? MouseButton.None : MouseButton.X1,
                    new(origin.X + input.X * scale, origin.Y + input.Y * scale), input.Milliseconds, ShortcutModifiers.None);
                var consumed = router.Route(packet, permission, pointerOverTarget: true);
                Assert.Equal(signal != MouseSignal.Move, consumed);

                var frame = native.Process(routed[^1].Input);
                var expected = lab.Process(input);
                Assert.Equal(expected.Update, frame.Update);
                Assert.Equal(expected.MatchedBinding, frame.Binding);
                if (frame.Update.CommittedDirection.HasValue)
                    Assert.Equal(expected.Preview?.Shortcut, frame.Binding is { Enabled: true } ? frame.Binding.Shortcut : null);
            }
        }
    }

    [Fact]
    public void UnknownWindowMismatchingModifiersAndInjectedEventsPassThrough()
    {
        var seen = new List<CapturedInput>();
        var router = new MouseGestureRouter(seen.Add);
        var permission = Permission(DefaultGestureProfiles.Create(GestureMode.Four), GestureTrigger.MouseX1);
        Assert.False(router.Route(Packet(MouseSignal.Down, 0, modifiers: ShortcutModifiers.Alt) with { Button = MouseButton.X1 }, permission, true));
        Assert.False(router.Route(Packet(MouseSignal.Down, 1) with { Button = MouseButton.X1 }, null, true));
        Assert.False(router.Route(Packet(MouseSignal.Down, 2) with { Button = MouseButton.X1 }, permission, false));
        Assert.False(router.Route(Packet(MouseSignal.Down, 3) with { Button = MouseButton.X1, Injected = true }, permission, true));
        Assert.Empty(seen);
    }

    [Theory]
    [InlineData(GestureTrigger.MouseX1, 2)]
    [InlineData(GestureTrigger.MouseX2, 3)]
    public void SideTriggerConsumesOnlyItsOwnPair(GestureTrigger trigger, int nativeButton)
    {
        var seen = new List<CapturedInput>();
        var router = new MouseGestureRouter(seen.Add);
        var permission = Permission(DefaultGestureProfiles.Create(GestureMode.Four), trigger);
        var button = (MouseButton)nativeButton;
        Assert.False(router.Route(Packet(MouseSignal.Down, 0), permission, true));
        Assert.True(router.Route(Packet(MouseSignal.Down, 1) with { Button = button }, permission, true));
        Assert.True(router.Route(Packet(MouseSignal.Up, 2) with { Button = button }, permission, true));
        Assert.Equal([GestureInputKind.Press, GestureInputKind.Release], seen.Select(frame => frame.Input.Kind));
    }

    [Theory]
    [InlineData(GestureTrigger.RightButtonDrag, ShortcutModifiers.None)]
    [InlineData(GestureTrigger.AltRightButtonDrag, ShortcutModifiers.Alt)]
    [InlineData(GestureTrigger.ShiftRightButtonDrag, ShortcutModifiers.Shift)]
    public void LegacyRightButtonSettingsNeverCaptureNativeRightClick(GestureTrigger trigger, ShortcutModifiers modifiers)
    {
        var seen = new List<CapturedInput>();
        var router = new MouseGestureRouter(seen.Add);
        var grant = Permission(DefaultGestureProfiles.Create(GestureMode.Four), trigger);
        Assert.False(router.Route(Packet(MouseSignal.Down, 0, modifiers: modifiers), grant, true));
        Assert.False(router.Route(Packet(MouseSignal.Up, 500, 100, modifiers), grant, true));
        Assert.Empty(seen);
    }

    [Fact]
    public void SequentialShortcutRoundTripsWithoutMergingItsSteps()
    {
        var sequence = Assert.IsType<KeyboardShortcut>(GestureFlow.UI.ShortcutRecorder.ParseSequence("ctrl+k，ctrl+c, Enter, Enter"));
        Assert.True(sequence.IsValid());
        Assert.Equal(ShortcutKey.K, sequence.Key);
        Assert.Equal([ShortcutKey.C, ShortcutKey.Enter, ShortcutKey.Enter], sequence.FollowingSteps.Select(step => step.Key));
        Assert.Equal(ShortcutModifiers.Control, sequence.FollowingSteps[0].Modifiers);
        Assert.Equal(ShortcutModifiers.None, sequence.FollowingSteps[1].Modifiers);
        Assert.Equal("Ctrl+K, Ctrl+C, Enter, Enter", GestureFlow.UI.GestureLabels.Shortcut(sequence));
        Assert.Null(GestureFlow.UI.ShortcutRecorder.ParseSequence("Ctrl+K,"));

        var editor = new GestureProfileEditor(DefaultGestureProfiles.Create(GestureMode.Four));
        Assert.True(editor.SetShortcut(GestureDirection.North, sequence));
        var restored = GestureProfileJson.Deserialize(GestureProfileJson.Serialize(editor.Profile));
        Assert.Equal(sequence, restored.GetEnabledBinding(GestureDirection.North)!.Shortcut);
    }

    [Fact]
    public void TypedSystemShortcutAcceptsAliasesAndRejectsPartialBindings()
    {
        Assert.Equal(new KeyboardShortcut(ShortcutKey.Escape, ShortcutModifiers.Control | ShortcutModifiers.Shift),
            GestureFlow.UI.ShortcutRecorder.Parse("ctrl + Shift + esc"));
        Assert.Null(GestureFlow.UI.ShortcutRecorder.Parse("Ctrl+"));
        Assert.Null(GestureFlow.UI.ShortcutRecorder.Parse("Shift+UnknownKey"));
    }

    [Fact]
    public void FocusPauseOrRevocationCancelsWithoutReplayingTheOwnedPair()
    {
        var permission = Permission(DefaultGestureProfiles.Create(GestureMode.Four), GestureTrigger.MouseX1);
        foreach (var changed in new[] { null, permission with { Revision = 7 }, permission with { FocusEpoch = 12 },
            permission with { Window = 205 }, permission with { KeyboardFocus = 301 },
            permission with { Profile = DefaultGestureProfiles.Create(GestureMode.Eight) } })
        {
            var seen = new List<CapturedInput>();
            var router = new MouseGestureRouter(seen.Add);
            Assert.True(router.Route(Packet(MouseSignal.Down, 0) with { Button = MouseButton.X1 }, permission, true));
            Assert.True(router.Route(Packet(MouseSignal.Up, 100) with { Button = MouseButton.X1 }, changed, true));
            Assert.Equal([GestureInputKind.Press, GestureInputKind.Cancel], seen.Select(frame => frame.Input.Kind));
            Assert.False(router.OwnsButton);
        }
    }

    [Fact]
    public void NativeKeyboardBatchPreservesHeldModifiersAndReleasesOnlyItsOwnKeys()
    {
        var batches = new List<WindowsInputNative.Input[]>();
        var executor = new WindowsActionExecutor(() => ShortcutModifiers.Control, _ => false, inputs =>
        {
            batches.Add(inputs);
            return (uint)inputs.Length;
        });
        var result = executor.TryExecute(new(ShortcutKey.Delete, ShortcutModifiers.Control | ShortcutModifiers.Shift,
            [ShortcutKey.F8]), () => true);
        Assert.Equal(ActionResult.Sent, result);
        var keys = Assert.Single(batches).Select(input => input.Data.Keyboard).ToArray();
        Assert.Equal(new ushort[] { 0xA0, 0x2E, 0x77, 0x77, 0x2E, 0xA0 }, keys.Select(key => key.Key));
        Assert.Equal(new uint[] { 0, 1, 0, 2, 3, 2 }, keys.Select(key => key.Flags));
        Assert.All(keys, key => Assert.Equal(WindowsInputNative.InjectionMarker, key.ExtraInfo));
    }

    [Fact]
    public void TargetChangesAndConflictingPhysicalKeysDoNotSend()
    {
        var calls = 0;
        var held = ShortcutModifiers.Alt;
        var executor = new WindowsActionExecutor(() => held, _ => false, _ => { calls++; return 0; });
        var shortcut = new KeyboardShortcut(ShortcutKey.S, ShortcutModifiers.Control);
        Assert.Equal(ActionResult.ConflictingKeyboardState, executor.TryExecute(shortcut, () => true));
        Assert.Equal(ActionResult.InvalidShortcut, executor.TryExecute(new((ShortcutKey)0, ShortcutModifiers.None), () => true));
        held = ShortcutModifiers.None;
        var checks = 0;
        Assert.Equal(ActionResult.UnsafeTarget, executor.TryExecute(shortcut, () => ++checks == 1));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PartialSubmissionOnlyReleasesAcceptedKeysAndDoesNotRetry()
    {
        var batches = new List<WindowsInputNative.Input[]>();
        var executor = new WindowsActionExecutor(() => ShortcutModifiers.None, _ => false, inputs =>
        {
            batches.Add(inputs);
            return batches.Count == 1 ? 1u : (uint)inputs.Length;
        });
        Assert.Equal(ActionResult.NativeFailure, executor.TryExecute(new(ShortcutKey.S, ShortcutModifiers.Control), () => true));
        Assert.Equal(2, batches.Count);
        var cleanup = Assert.Single(batches[1]).Data.Keyboard;
        Assert.Equal(0xA2, cleanup.Key);
        Assert.Equal(2u, cleanup.Flags);

        var zero = new WindowsActionExecutor(() => ShortcutModifiers.None, _ => false, inputs => { batches.Add(inputs); return 0; });
        Assert.Equal(ActionResult.NativeFailure, zero.TryExecute(new(ShortcutKey.S, ShortcutModifiers.Control), () => true));
        Assert.Equal(3, batches.Count);
        Assert.All(Enum.GetValues<ShortcutKey>(), key => Assert.NotEqual(0, WindowsActionExecutor.VirtualKey(key)));
    }

    private static MousePacket Packet(MouseSignal signal, long tick, double x = 0,
        ShortcutModifiers modifiers = ShortcutModifiers.None) =>
        new(signal, MouseButton.Right, new(x, 0), tick, modifiers);

    private static InputPermission Permission(GestureProfile profile, GestureTrigger trigger) =>
        new(101, 102, 10, 0, 0, long.MaxValue, "test", trigger, profile,
            new WindowsInputNative.Rect { Left = -1000, Top = -1000, Right = 1000, Bottom = 1000 });
}
