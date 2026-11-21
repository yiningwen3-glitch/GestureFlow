using System.Text.Json;
using System.Text.Json.Serialization;
using GestureFlow.Core;
using GestureFlow.Profiles;

namespace GestureFlow.Lab.Simulation;

public sealed record GestureTestCase(GestureProfile Profile, IReadOnlyList<TimedGestureInput> Events);

public static class GestureTestCaseJson
{
    private sealed record CaseData(int Version, JsonElement Profile, TimedGestureInput[] Events);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<GestureInputKind>(allowIntegerValues: false) }
    };

    public static string Serialize(GestureTestCase testCase)
    {
        var events = testCase.Events.ToArray();
        ValidateEvents(events);
        using var profile = JsonDocument.Parse(GestureProfileJson.Serialize(testCase.Profile));
        return JsonSerializer.Serialize(new CaseData(1, profile.RootElement, events), Options);
    }

    public static GestureTestCase Deserialize(string json)
    {
        var data = JsonSerializer.Deserialize<CaseData>(json, Options)
            ?? throw new JsonException("测试用例不能为空。");
        if (data.Version != 1)
            throw new JsonException("不支持此测试用例版本。");

        ValidateEvents(data.Events);
        return new(GestureProfileJson.Deserialize(data.Profile.GetRawText()), Array.AsReadOnly(data.Events));
    }

    private static void ValidateEvents(TimedGestureInput[]? events)
    {
        if (events is not { Length: > 0 } || events[0].Milliseconds != 0)
            throw new JsonException("测试用例需要一组从 0 毫秒开始的事件。");

        var previous = 0L;
        var machine = new GestureStateMachine(GestureMode.Eight);
        foreach (var input in events)
        {
            if (input.Milliseconds < previous || !Enum.IsDefined(input.Kind) || !double.IsFinite(input.X) || !double.IsFinite(input.Y))
                throw new JsonException("事件必须按时间排列，并包含有效类型和有限坐标。");

            previous = input.Milliseconds;
            machine.Process(input.ToGestureInput());
        }

        if (machine.State == GestureState.Tracking)
            throw new JsonException("最后一次按下必须释放或取消。");
    }
}
