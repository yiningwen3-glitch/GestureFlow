using System.IO;
using System.Security;
using System.Text.Json;
using GestureFlow.Profiles;

namespace GestureFlow.UI;

// Position is in virtual screen pixels, including monitors at negative coordinates.
public sealed record DockPosition(int X, int Y);

public sealed record FloatingDockPreferences(
    bool Visible = false, bool AlwaysOnTop = true, bool StartCollapsed = true,
    bool AutoCollapse = true, double Opacity = 1, DockPosition? Position = null)
{
    public const double MinimumOpacity = 0.45;

    public static FloatingDockPreferences Load(string path, out bool recovered)
    {
        recovered = false;
        try
        {
            var settings = JsonSerializer.Deserialize<FloatingDockPreferences>(File.ReadAllText(path));
            return settings is { Opacity: >= MinimumOpacity and <= 1 }
                ? settings : throw new JsonException("悬浮岛偏好无效。");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new();
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException or JsonException)
        {
            recovered = true;
            return new();
        }
    }

    public void Save(string path) => GestureProfileManager.AtomicWrite(path, JsonSerializer.Serialize(this));
}

public readonly record struct DockWorkArea(int Left, int Top, int Right, int Bottom)
{
    public DockPosition Fit(DockPosition? position, int width, int height)
    {
        var right = Math.Max(Left, Right - width);
        var bottom = Math.Max(Top, Bottom - height);
        return new(Math.Clamp(position?.X ?? right - 16, Left, right),
            Math.Clamp(position?.Y ?? bottom - 24, Top, bottom));
    }
}
