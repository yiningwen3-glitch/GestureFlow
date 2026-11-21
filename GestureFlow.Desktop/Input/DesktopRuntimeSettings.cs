using System.IO;
using System.Security;
using System.Text.Json;
using GestureFlow.Core;
using GestureFlow.Profiles;

namespace GestureFlow.Desktop.Input;

internal enum RuntimeSystemEvent { None, PowerTransition, ShellRestart, DisplayChanged }

internal static class DesktopRuntimeSettings
{
    public static GestureProfile LoadProfile(string path, out bool recovered) =>
        Read(path, GestureProfileJson.Deserialize, DefaultGestureProfiles.Create(GestureMode.Four), out recovered);

    public static int LoadHotkey(string path, int options, out bool recovered) =>
        Read(path, text =>
        {
            var value = JsonSerializer.Deserialize<int>(text);
            return value >= 0 && value < options ? value : throw new JsonException();
        }, 0, out recovered);

    public static DesktopWindowPreferences LoadWindowPreferences(string path, out bool recovered) =>
        Read(path, text =>
        {
            var value = JsonSerializer.Deserialize<DesktopWindowPreferences>(text);
            return value is not null && Enum.IsDefined(value.CloseAction) ? value : throw new JsonException();
        }, new DesktopWindowPreferences(), out recovered);

    public static RuntimeSystemEvent SystemEvent(int message, nint parameter, uint shellRestartMessage)
    {
        if (message == 0x0218 && (parameter == 4 || parameter == 7 || parameter == 18))
            return RuntimeSystemEvent.PowerTransition;
        if (shellRestartMessage != 0 && (uint)message == shellRestartMessage)
            return RuntimeSystemEvent.ShellRestart;
        return message == 0x007E ? RuntimeSystemEvent.DisplayChanged : RuntimeSystemEvent.None;
    }

    private static T Read<T>(string path, Func<string, T> parse, T fallback, out bool recovered)
    {
        recovered = false;
        try { return parse(File.ReadAllText(path)); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return fallback;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or JsonException or ProfileValidationException)
        {
            recovered = true;
            return fallback;
        }
    }
}
