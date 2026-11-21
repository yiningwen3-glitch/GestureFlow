using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace GestureFlow.Desktop.Applications;

internal static class ExecutableInstallationStamp
{
    public static string? Read(string? path)
    {
        path = ApplicationIdentity.NormalizeExecutable(path);
        if (path is null || path.StartsWith(@"\\", StringComparison.Ordinal))
            return null;

        try
        {
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
                return null;

            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandleEx(file, 18, out FileIdentity identity, (uint)Marshal.SizeOf<FileIdentity>())
                || !GetFileInformationByHandleEx(file, 0, out FileTimes times, (uint)Marshal.SizeOf<FileTimes>()))
                return null;

            return FormattableString.Invariant(
                $"ntfs1:{identity.VolumeSerial:x16}:{identity.FileIdPartA:x16}:{identity.FileIdPartB:x16}:{times.Creation:x16}:{times.Write:x16}:{times.Change:x16}:{RandomAccess.GetLength(file):x16}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong VolumeSerial, FileIdPartA, FileIdPartB;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTimes
    {
        public long Creation, Access, Write, Change;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out FileIdentity information, uint informationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out FileTimes information, uint informationSize);
}
