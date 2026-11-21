using System.Text.Json.Serialization;
using GestureFlow.Core;

namespace GestureFlow.Profiles;

public enum GestureProfileScope { Global, Application }

public sealed record ManagedGestureProfile(Guid ProfileId, string Name, GestureProfileScope Scope,
    string? AppId, int SectorCount, IReadOnlyList<GestureBinding> Gestures, bool ReadOnly,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    [JsonIgnore]
    public GestureProfile Configuration { get; internal init; } = new((GestureMode)SectorCount, Gestures, []);
}
