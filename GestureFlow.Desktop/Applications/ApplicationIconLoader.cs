using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GestureFlow.Desktop.Applications;

internal static class ApplicationIconLoader
{
    private const uint GetSmallIcon = 0x100 | 0x1;
    private const uint IdList = 0x8;

    public static Dictionary<string, ImageSource?> Load(IEnumerable<InstalledApplication> applications, CancellationToken token)
    {
        var initialized = CoInitializeEx(0, 0) >= 0;
        try
        {
            var icons = new Dictionary<string, ImageSource?>(StringComparer.Ordinal);
            var cache = new Dictionary<ApplicationIcon, ImageSource?>();
            foreach (var application in applications)
            {
                token.ThrowIfCancellationRequested();
                if (application.Icon is not { } icon)
                    continue;
                if (!cache.TryGetValue(icon, out var image))
                    cache[icon] = image = Load(icon);
                if (image is null && application.ExecutablePath is { } path && path != icon.Value)
                {
                    var fallback = new ApplicationIcon(path);
                    if (!cache.TryGetValue(fallback, out image))
                        cache[fallback] = image = Load(fallback);
                }
                icons[application.AppId] = image;
            }
            return icons;
        }
        finally
        {
            if (initialized)
                CoUninitialize();
        }
    }

    private static ImageSource? Load(ApplicationIcon icon)
    {
        nint handle = 0;
        try
        {
            var size = (uint)Marshal.SizeOf<ShellFileInfo>();
            if (icon.IsShellItem)
            {
                if (SHParseDisplayName(icon.Value, 0, out var id, 0, out _) >= 0 && id != 0)
                {
                    try { SHGetFileInfoFromId(id, 0, out var info, size, GetSmallIcon | IdList); handle = info.Icon; }
                    finally { Marshal.FreeCoTaskMem(id); }
                }
            }
            else if (RegistryApplicationSource.IsLocalFile(icon.Value))
            {
                if (icon.Index != 0)
                    ExtractIconEx(icon.Value, icon.Index, 0, out handle, 1);
                if (handle == 0)
                {
                    SHGetFileInfo(icon.Value, 0, out var info, size, GetSmallIcon);
                    handle = info.Icon;
                }
            }

            if (handle == 0)
                return null;

            var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (handle != 0)
                DestroyIcon(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SHGetFileInfoFromId(nint pidl, uint attributes, out ShellFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, nint context, out nint pidl, uint mask, out uint attributes);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, nint largeIcon, out nint smallIcon, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint mode);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
