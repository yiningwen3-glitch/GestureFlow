using System.Text.Json;
using System.Text.Json.Serialization;
using GestureFlow.Core;

namespace GestureFlow.Profiles;

public sealed class ProfileValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class GestureProfileJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        WriteIndented = true,
        Converters = { new ShortcutModifiersConverter(), new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static string Serialize(GestureProfile profile)
    {
        EnsureValid(profile);
        return JsonSerializer.Serialize(profile, Options);
    }

    public static GestureProfile Deserialize(string json)
    {
        var profile = JsonSerializer.Deserialize<GestureProfile>(json, Options)
            ?? throw new JsonException("A gesture profile object is required.");
        EnsureValid(profile);
        return profile;
    }

    private static void EnsureValid(GestureProfile profile)
    {
        var errors = GestureProfileValidator.Validate(profile);
        if (errors.Count > 0)
            throw new ProfileValidationException(errors);
    }

    private sealed class ShortcutModifiersConverter : JsonConverter<ShortcutModifiers>
    {
        private static readonly ShortcutModifiers[] KnownModifiers =
            [ShortcutModifiers.Control, ShortcutModifiers.Shift, ShortcutModifiers.Alt, ShortcutModifiers.Windows];

        public override ShortcutModifiers Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Shortcut modifiers must be an array.");

            var modifiers = ShortcutModifiers.None;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException("A shortcut modifier must be a string.");

                var modifier = reader.GetString() switch
                {
                    nameof(ShortcutModifiers.Control) => ShortcutModifiers.Control,
                    nameof(ShortcutModifiers.Shift) => ShortcutModifiers.Shift,
                    nameof(ShortcutModifiers.Alt) => ShortcutModifiers.Alt,
                    nameof(ShortcutModifiers.Windows) => ShortcutModifiers.Windows,
                    _ => throw new JsonException("Unknown shortcut modifier.")
                };
                if ((modifiers & modifier) != 0)
                    throw new JsonException("A shortcut modifier appears more than once.");
                modifiers |= modifier;
            }

            if (reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException("The shortcut modifiers array is incomplete.");
            return modifiers;
        }

        public override void Write(Utf8JsonWriter writer, ShortcutModifiers value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var modifier in KnownModifiers)
            {
                if ((value & modifier) != 0)
                    writer.WriteStringValue(modifier.ToString());
            }
            writer.WriteEndArray();
        }
    }
}
