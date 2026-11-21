using System.Text.Json;
using System.Text.Json.Nodes;
using GestureFlow.Core;
using GestureFlow.Profiles;

namespace GestureFlow.Tests;

public sealed class GestureProfileTests
{
    [Theory]
    [InlineData(GestureMode.Four, 4)]
    [InlineData(GestureMode.Eight, 8)]
    public void DefaultsAreValidAndOptInForApplications(GestureMode mode, int count)
    {
        var profile = DefaultGestureProfiles.Create(mode);
        Assert.Empty(GestureProfileValidator.Validate(profile));
        Assert.Equal(count, profile.Bindings.Count);
        Assert.Equal(DirectionRecognizer.GetDirections(mode), profile.Bindings.Select(binding => binding.Direction));
        Assert.All(profile.Bindings, binding => Assert.True(binding.Enabled));
        Assert.Equal(new(ShortcutKey.C, ShortcutModifiers.Control), profile.GetEnabledBinding(GestureDirection.North)?.Shortcut);
        Assert.Equal(ApplicationGestureStatus.Disabled, profile.GetApplicationStatus("app.exe"));
    }

    [Fact]
    public void RoundTripPreservesCustomNamesStructuredShortcutsAndApplicationDecisions()
    {
        var custom = new GestureBinding(GestureDirection.North, "自定义动作",
            new(ShortcutKey.F12, ShortcutModifiers.Control | ShortcutModifiers.Shift, [ShortcutKey.F11]), false);
        var profile = ProfileWithNorth(custom) with
        {
            Applications = [new("CAD.exe", true, true, true), new("Viewer.exe", false, false, false)]
        };

        var json = GestureProfileJson.Serialize(profile);
        using var parsed = JsonDocument.Parse(json);
        var shortcut = parsed.RootElement.GetProperty("bindings")[0].GetProperty("shortcut");
        Assert.Equal("F12", shortcut.GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Array, shortcut.GetProperty("modifiers").ValueKind);
        Assert.Equal("F11", shortcut.GetProperty("additionalKeys")[0].GetString());
        Assert.False(parsed.RootElement.GetProperty("bindings")[1].GetProperty("shortcut").TryGetProperty("additionalKeys", out _));
        var restored = GestureProfileJson.Deserialize(json);

        Assert.Equal(profile.Mode, restored.Mode);
        Assert.Equal<GestureBinding>(profile.Bindings, restored.Bindings);
        Assert.Equal<ApplicationRule>(profile.Applications, restored.Applications);
        Assert.Null(restored.GetEnabledBinding(custom.Direction));
        Assert.Equal(ApplicationGestureStatus.Enabled, restored.GetApplicationStatus("cad.EXE"));
        Assert.Equal(ApplicationGestureStatus.Disabled, restored.GetApplicationStatus("viewer.exe"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"mode\":\"Four\",\"bindings\":[],\"applications\":[],\"applications\":[]}")]
    [InlineData("{\"mode\":4,\"bindings\":[],\"applications\":[]}")]
    [InlineData("{\"mode\":\"six\",\"bindings\":[],\"applications\":[]}")]
    [InlineData("{\"mode\":\"Four\",\"bindings\":[],\"applications\":[],\"unexpected\":true}")]
    [InlineData("{\"mode\":\"Four\",\"bindings\":[]}")]
    public void InvalidJsonShapesAreRejected(string json) =>
        Assert.Throws<JsonException>(() => GestureProfileJson.Deserialize(json));

    [Fact]
    public void MissingShortcutKeyIsRejected()
    {
        var json = DefaultJson();
        json["bindings"]![0]!["shortcut"]!.AsObject().Remove("key");
        Assert.Throws<JsonException>(() => GestureProfileJson.Deserialize(json.ToJsonString()));
    }

    [Theory]
    [InlineData("\"Control+Shift\"")]
    [InlineData("[\"Control\",\"Control\"]")]
    [InlineData("[\"Unknown\"]")]
    [InlineData("[1]")]
    public void ModifiersRequireAListOfUniqueSupportedNames(string value)
    {
        var json = DefaultJson();
        json["bindings"]![0]!["shortcut"]!["modifiers"] = JsonNode.Parse(value);
        Assert.Throws<JsonException>(() => GestureProfileJson.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void NullArrayEntriesAreValidationErrors()
    {
        var json = DefaultJson();
        json["bindings"]![0] = null;
        json["applications"]!.AsArray().Add((JsonNode?)null);
        var error = Assert.Throws<ProfileValidationException>(() => GestureProfileJson.Deserialize(json.ToJsonString()));
        Assert.Contains(error.Errors, item => item.StartsWith("bindings[0]"));
        Assert.Contains(error.Errors, item => item.StartsWith("applications[0]"));
    }

    [Fact]
    public void AProfileMustHaveOneBindingPerAvailableDirection()
    {
        var profile = DefaultGestureProfiles.Create(GestureMode.Four);
        var invalid = profile with { Bindings = [profile.Bindings[0], profile.Bindings[0], profile.Bindings[1], profile.Bindings[2], new(GestureDirection.NorthEast, "斜向", new(ShortcutKey.F2, ShortcutModifiers.None), true)] };
        var error = Assert.Throws<ProfileValidationException>(() => GestureProfileJson.Serialize(invalid));
        Assert.Contains(error.Errors, item => item.Contains("duplicate direction"));
        Assert.Contains(error.Errors, item => item.Contains("missing direction West"));
        Assert.Contains(error.Errors, item => item.Contains("unavailable"));
    }

    [Fact]
    public void BlankNameAndOutOfRangeShortcutAreRejected()
    {
        var invalid = ProfileWithNorth(new(GestureDirection.North, "  ",
            new((ShortcutKey)0, (ShortcutModifiers)128), true));
        var error = Assert.Throws<ProfileValidationException>(() => GestureProfileJson.Serialize(invalid));
        Assert.Contains(error.Errors, item => item.Contains("displayName"));
        Assert.Contains(error.Errors, item => item.Contains("shortcut"));
        Assert.False(new KeyboardShortcut(ShortcutKey.F2, (ShortcutModifiers)128).IsValid());
        Assert.False(new KeyboardShortcut((ShortcutKey)0, ShortcutModifiers.None).IsValid());
    }

    [Fact]
    public void ShortcutWithoutModifiersRoundTrips()
    {
        var binding = new GestureBinding(GestureDirection.North, "帮助", new(ShortcutKey.F1, ShortcutModifiers.None), true);
        var json = GestureProfileJson.Serialize(ProfileWithNorth(binding));
        Assert.Equal(binding, GestureProfileJson.Deserialize(json).GetEnabledBinding(binding.Direction));
    }

    [Fact]
    public void ApplicationsShareBindingsAndKnownConflictsRequireConfirmation()
    {
        var profile = DefaultGestureProfiles.Create(GestureMode.Four) with
        {
            Applications = [new("writer.exe", true, false, false), new("cad.exe", true, true, false)]
        };

        Assert.Equal(ApplicationGestureStatus.Enabled, profile.GetApplicationStatus("writer.exe"));
        Assert.Equal(ApplicationGestureStatus.ConfirmationRequired, profile.GetApplicationStatus("writer.exe", potentialRightClickConflict: true));
        Assert.Equal(ApplicationGestureStatus.ConfirmationRequired, profile.GetApplicationStatus("CAD.exe"));
        Assert.Equal(ApplicationGestureStatus.Disabled, profile.GetApplicationStatus(null));
        Assert.Throws<ProfileValidationException>(() => GestureProfileJson.Serialize(profile));

        var confirmed = profile with { Applications = [profile.Applications[0], profile.Applications[1] with { RightClickConflictConfirmed = true }] };
        Assert.Empty(GestureProfileValidator.Validate(confirmed));
        Assert.Equal(ApplicationGestureStatus.Enabled, confirmed.GetApplicationStatus("cad.exe"));
        Assert.Same(profile.Bindings, confirmed.Bindings);
    }

    [Fact]
    public void ApplicationValidationRejectsPathsDuplicateNamesAndUnassociatedConfirmation()
    {
        var profile = DefaultGestureProfiles.Create(GestureMode.Four) with
        {
            Applications = [new("writer.exe", true, false, false), new("WRITER.EXE", false, false, false),
                new(@"C:\tools\cad.exe", false, false, false), new("viewer.exe", false, false, true)]
        };
        var errors = GestureProfileValidator.Validate(profile);
        Assert.Contains(errors, item => item.Contains("duplicate application"));
        Assert.Contains(errors, item => item.Contains("file name"));
        Assert.Contains(errors, item => item.Contains("requires a recorded"));
    }

    private static GestureProfile ProfileWithNorth(GestureBinding binding)
    {
        var profile = DefaultGestureProfiles.Create(GestureMode.Four);
        return profile with { Bindings = [binding, .. profile.Bindings.Skip(1)] };
    }

    private static JsonNode DefaultJson() =>
        JsonNode.Parse(GestureProfileJson.Serialize(DefaultGestureProfiles.Create(GestureMode.Four)))!;
}
