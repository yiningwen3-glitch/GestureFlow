using System.IO;
using System.Runtime.InteropServices;

namespace GestureFlow.Desktop;

internal static class ShellShortcuts
{
    public static void CreateStartMenu(string programsDirectory, string executable) =>
        Write(GetShortcutPath(programsDirectory, "GestureFlow", "GestureFlow.lnk"), executable);

    public static bool IsStartupEnabled(string startupDirectory) => File.Exists(StartupPath(startupDirectory));

    public static void SetStartup(string startupDirectory, string executable, bool enabled)
    {
        var shortcut = StartupPath(startupDirectory);
        if (enabled)
            Write(shortcut, executable);
        else
            File.Delete(shortcut);
    }

    private static string StartupPath(string directory) => GetShortcutPath(directory, "GestureFlow.Desktop.lnk");

    private static string GetShortcutPath(string directory, params string[] relativePath)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new DirectoryNotFoundException("找不到当前用户的快捷方式目录。");

        return Path.Combine(directory, Path.Combine(relativePath));
    }

    private static void Write(string path, string executable)
    {
        if (!File.Exists(executable))
            throw new FileNotFoundException("找不到 GestureFlow.Desktop.exe，请从构建输出或便携包运行。");

        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $"{Guid.NewGuid():N}.lnk");
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        try
        {
            var link = ((dynamic)shell).CreateShortcut(temporary);
            try
            {
                link.TargetPath = executable;
                link.WorkingDirectory = Path.GetDirectoryName(executable);
                link.Description = "GestureFlow 鼠标手势";
                link.Save();
            }
            finally { Marshal.FinalReleaseComObject(link); }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
            File.Delete(temporary);
        }
    }
}
