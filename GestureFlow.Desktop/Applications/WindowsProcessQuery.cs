using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GestureFlow.Desktop.Applications;

internal static class WindowsProcessQuery
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private static readonly Lazy<uint?> SenderIntegrity = new(() => ReadIntegrityLevel((uint)Environment.ProcessId));

    public static SafeProcessHandle OpenForQuery(uint processId) =>
        OpenProcess(QueryLimitedInformation, false, processId);

    public static bool CanSendInputTo(uint processId) =>
        processId == (uint)Environment.ProcessId
        || (processId != 0 && SenderIntegrity.Value is { } sender
            && ReadIntegrityLevel(processId) is { } target && target <= sender);

    private static uint? ReadIntegrityLevel(uint processId)
    {
        using var process = OpenForQuery(processId);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
            return null;

        using (token)
        {
            GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var length);
            if (length < IntPtr.Size)
                return null;

            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
                    return null;
                var sid = Marshal.ReadIntPtr(buffer);
                if (sid == 0)
                    return null;
                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count > 0 ? unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)count - 1))) : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, nint buffer, int length, out int returned);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint index);
}
