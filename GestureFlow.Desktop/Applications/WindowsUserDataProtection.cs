using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GestureFlow.Desktop.Applications;

internal static class WindowsUserDataProtection
{
    public static byte[] Protect(byte[] data) => Transform(data, protect: true);
    public static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        const uint noPrompt = 1;
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        Blob output = default;
        try
        {
            var input = new Blob { Count = bytes.Length, Data = pin.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, 0, 0, 0, 0, noPrompt, out output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, noPrompt, out output);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[output.Count];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != 0)
                LocalFree(output.Data);
            pin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Count;
        public nint Data;
    }

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, nint description, nint entropy, nint reserved,
        nint prompt, uint flags, out Blob output);

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved,
        nint prompt, uint flags, out Blob output);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);
}
