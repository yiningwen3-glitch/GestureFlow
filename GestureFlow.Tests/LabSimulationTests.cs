using System.Collections.Immutable;
using System.Text.Json;
using GestureFlow.Core;
using GestureFlow.Lab.Simulation;
using GestureFlow.Profiles;

namespace GestureFlow.Tests;

public sealed class LabSimulationTests
{
    public static TheoryData<GestureMode, GestureDirection, GestureDirection> Directions()
    {
        var data = new TheoryData<GestureMode, GestureDirection, GestureDirection>();
        foreach (var direction in Enum.GetValues<GestureDirection>())
        {
            data.Add(GestureMode.Eight, direction, direction);
            var fourWay = (GestureDirection)(((int)direction + 1) / 2 * 2 % 8);
            data.Add(GestureMode.Four, direction, fourWay);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void DirectionScenariosUseTheSameVirtualPipeline(
        GestureMode mode, GestureDirection dragDirection, GestureDirection expected)
    {
        var profile = DefaultGestureProfiles.Create(mode);
        var traces = Run(profile, GestureScenarios.Direction(dragDirection));
        Assert.Null(traces[1].Preview);
        Assert.Equal(expected, traces[2].Update.Candidate);
        Assert.Null(traces[2].Preview);
        Assert.Equal(expected, traces[^1].Update.CommittedDirection);
        Assert.Equal(profile.GetEnabledBinding(expected)!.Shortcut, traces[^1].Preview!.Shortcut);
        Assert.Equal(GestureState.Idle, traces[^1].Update.State);
        Assert.Single(traces, trace => trace.Outcome == GestureOutcome.Selected);
        Assert.Equal([0L, 65, 125, 190], traces.Select(trace => trace.Input.Milliseconds));
    }

    [Theory]
    [InlineData(GestureScenario.Click, GestureOutcome.Click)]
    [InlineData(GestureScenario.LongPress, GestureOutcome.LongPress)]
    [InlineData(GestureScenario.Invalid, GestureOutcome.DeadZone)]
    [InlineData(GestureScenario.Cancel, GestureOutcome.Cancelled)]
    public void NonSelectingScenariosFinishIdleWithoutPreview(GestureScenario scenario, GestureOutcome expected)
    {
        var traces = Run(DefaultGestureProfiles.Create(GestureMode.Eight), GestureScenarios.Create(scenario));
        Assert.Equal(expected, traces[^1].Outcome);
        Assert.Equal(GestureState.Idle, traces[^1].Update.State);
        Assert.All(traces, trace => Assert.Null(trace.Preview));
    }

    [Fact]
    public void RapidScenarioProducesThreeIndependentPreviews()
    {
        var traces = Run(DefaultGestureProfiles.Create(GestureMode.Four), GestureScenarios.Create(GestureScenario.Rapid));
        Assert.Equal([GestureDirection.North, GestureDirection.East, GestureDirection.South],
            traces.Where(trace => trace.Preview is not null).Select(trace => trace.Preview!.Direction));
    }

    [Theory]
    [InlineData(41, GestureDirection.North)]
    [InlineData(42, GestureDirection.NorthEast)]
    [InlineData(-41, GestureDirection.North)]
    [InlineData(-42, GestureDirection.NorthWest)]
    public void ReplayPreservesEightWayAngleBoundaries(int x, GestureDirection expected)
    {
        var testCase = new GestureTestCase(DefaultGestureProfiles.Create(GestureMode.Eight),
            [new(0, GestureInputKind.Press, 100, 200), new(25, GestureInputKind.Move, 100 + x, 100), new(55, GestureInputKind.Release, 100 + x, 100)]);
        var restored = GestureTestCaseJson.Deserialize(GestureTestCaseJson.Serialize(testCase));
        var trace = Run(restored.Profile, restored.Events)[^1];
        Assert.Equal(expected, trace.Preview!.Direction);
        Assert.Equal(double.Hypot(x, 100), trace.Distance);
    }

    [Fact]
    public void EditorRejectsInvalidEditsAndRetainsCustomBindingsAcrossModeChanges()
    {
        var profile = DefaultGestureProfiles.Create(GestureMode.Four) with
        {
            Applications = [new("writer.exe", true, false, false)]
        };
        var editor = new LabProfileEditor(profile);
        var shortcut = new KeyboardShortcut(ShortcutKey.S, ShortcutModifiers.Control);
        Assert.True(editor.SetName(GestureDirection.North, "快速保存"));
        Assert.True(editor.SetShortcut(GestureDirection.North, shortcut));
        Assert.False(editor.SetName(GestureDirection.North, " "));
        Assert.False(editor.SetShortcut(GestureDirection.North, new((ShortcutKey)0, ShortcutModifiers.Control)));
        Assert.False(editor.SetShortcut(GestureDirection.North, new(ShortcutKey.S, (ShortcutModifiers)16)));

        editor.ChangeMode(GestureMode.Eight);
        Assert.True(editor.SetName(GestureDirection.NorthEast, "备用命令"));
        editor.ChangeMode(GestureMode.Four);
        editor.ChangeMode(GestureMode.Eight);
        var reloaded = GestureProfileJson.Deserialize(GestureProfileJson.Serialize(editor.Profile));
        Assert.Equal("备用命令", reloaded.GetEnabledBinding(GestureDirection.NorthEast)!.DisplayName);
        var preview = Run(reloaded, GestureScenarios.Direction(GestureDirection.North))[^1].Preview;
        Assert.Equal(new GesturePreview(GestureDirection.North, "快速保存", shortcut), preview);

        editor.RestoreDefaults();
        Assert.Equal(GestureMode.Eight, editor.Profile.Mode);
        Assert.Empty(editor.Profile.Applications);
        Assert.Equal("复制", editor.Profile.GetEnabledBinding(GestureDirection.North)!.DisplayName);
        Assert.Equal("重做", editor.Profile.GetEnabledBinding(GestureDirection.NorthEast)!.DisplayName);
    }

    [Fact]
    public void SnapshotReplayMatchesAfterTheActiveProfileChanges()
    {
        var editor = new LabProfileEditor(DefaultGestureProfiles.Create(GestureMode.Four));
        editor.SetName(GestureDirection.North, "快速保存");
        editor.SetShortcut(GestureDirection.North, new(ShortcutKey.S, ShortcutModifiers.Control));
        var testCase = new GestureTestCase(editor.Profile, GestureScenarios.Create(GestureScenario.Rapid));
        var original = Run(testCase.Profile, testCase.Events);
        var json = GestureTestCaseJson.Serialize(testCase);

        editor.RestoreDefaults();
        editor.ChangeMode(GestureMode.Eight);
        var restored = GestureTestCaseJson.Deserialize(json);
        Assert.Equal(GestureMode.Four, restored.Profile.Mode);
        Assert.Equal(original, Run(restored.Profile, restored.Events));
        Assert.Equal(original, Run(restored.Profile, restored.Events));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"milliseconds\":1,\"kind\":\"Cancel\"}]")]
    [InlineData("[{\"milliseconds\":0,\"kind\":\"Press\"}]")]
    [InlineData("[{\"milliseconds\":0,\"kind\":\"Press\"},{\"milliseconds\":10,\"kind\":\"Move\"},{\"milliseconds\":2,\"kind\":\"Release\"}]")]
    [InlineData("[{\"milliseconds\":0,\"kind\":6}]")]
    [InlineData("[{\"milliseconds\":0,\"kind\":\"Move\",\"x\":1e999}]")]
    public void InvalidRecordingsCannotReplaceAValidTestCase(string events)
    {
        var profile = GestureProfileJson.Serialize(DefaultGestureProfiles.Create(GestureMode.Four));
        var json = $$"""{"version":1,"profile":{{profile}},"events":{{events}}}""";
        Assert.Throws<JsonException>(() => GestureTestCaseJson.Deserialize(json));
    }

    [Fact]
    public void DisabledDirectionIsDiagnosedButNeverExecuted()
    {
        var profile = DefaultGestureProfiles.Create(GestureMode.Four);
        var north = profile.Bindings[0] with { Enabled = false };
        profile = profile with { Bindings = [north, .. profile.Bindings.Skip(1)] };

        var trace = Run(profile, GestureScenarios.Direction(GestureDirection.North))[^1];
        Assert.Equal(north, trace.MatchedBinding);
        Assert.Null(trace.Preview);
        Assert.Equal(GestureOutcome.Disabled, trace.Outcome);
    }

    [Fact]
    public void ReleaseOverAnUnboundDirectionDoesNotExecuteTheEarlierCandidate()
    {
        var profile = DefaultGestureProfiles.Create(GestureMode.Four);
        profile = profile with { Bindings = profile.Bindings.Where(binding => binding.Direction != GestureDirection.North).ToImmutableArray() };
        var traces = Run(profile,
        [
            new(0, GestureInputKind.Press),
            new(1, GestureInputKind.Move, 300, 0),
            new(2, GestureInputKind.Move, 0, -300),
            new(3, GestureInputKind.Release, 0, -300)
        ]);
        Assert.Equal(GestureDirection.East, traces[1].Update.Candidate);
        Assert.Equal(GestureDirection.North, traces[^1].Update.CommittedDirection);
        Assert.Equal(GestureOutcome.Disabled, traces[^1].Outcome);
        Assert.All(traces, trace => Assert.Null(trace.Preview));
        Assert.Equal(GestureState.Idle, traces[^1].Update.State);
    }

    [Fact]
    public void PreviewExecutorCannotRecordAnInvalidShortcut()
    {
        var executor = new PreviewActionExecutor();
        var shortcut = new KeyboardShortcut(ShortcutKey.S, ShortcutModifiers.Control);
        executor.Execute(shortcut);
        Assert.Throws<ArgumentException>(() => executor.Execute(new((ShortcutKey)0, ShortcutModifiers.None)));
        Assert.Equal(shortcut, executor.LastShortcut);
    }

    private static GestureTrace[] Run(GestureProfile profile, IReadOnlyList<TimedGestureInput> inputs)
    {
        var session = new GestureLabSession(profile);
        return inputs.Select(session.Process).ToArray();
    }
}
