using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GestureFlow.Core.Protection;
using GestureFlow.Profiles;
using Microsoft.Win32.SafeHandles;

namespace GestureFlow.Desktop.Applications;

public enum ForegroundWindowKind { DesktopProgram, PackagedApp, ApplicationHost, ExplorerDesktop, ExplorerFolder }

public sealed record RunningApplication(nint WindowHandle, int WindowOwnerProcessId, int EffectiveProcessId,
    string WindowClass, ForegroundWindowKind WindowKind, string? ExecutableName, string? ExecutablePath,
    string? Aumid, string? PackageFamilyName, string? InstallationStamp = null);

internal sealed record ForegroundGestureTarget(InstalledApplication? Application, AppRiskAssessment Risk,
    ProtectionStatus Protection, GestureAuthorization? Authorization, ManagedGestureProfile? Profile);

public sealed class ForegroundApplicationService
{
    public RunningApplication? GetForegroundApplication()
    {
        var window = Native.GetForegroundWindow();
        var result = InspectWindow(window);
        if (result is null || window != Native.GetForegroundWindow())
            return null;

        var stamp = result.WindowOwnerProcessId == Environment.ProcessId
            ? null : ExecutableInstallationStamp.Read(result.ExecutablePath);
        return IsCurrent(result) ? result with { InstallationStamp = stamp } : null;
    }

    internal static ForegroundWindowIdentity ForegroundWindow()
    {
        var window = Native.GetForegroundWindow();
        return window != 0 && Native.GetWindowThreadProcessId(window, out var owner) != 0 ? new(window, (int)owner) : default;
    }

    internal static bool IsLive(RunningApplication running) =>
        Native.GetWindowThreadProcessId(running.WindowHandle, out var owner) != 0
        && owner == (uint)running.WindowOwnerProcessId;

    internal static bool IsCurrent(RunningApplication running) =>
        running.WindowHandle == Native.GetForegroundWindow() && IsLive(running);

    internal static ForegroundGestureTarget ResolveGestures(RunningApplication running, IEnumerable<InstalledApplication> catalog,
        AppRiskPolicyService risks, ProtectionPolicyService protection, GestureProfileManager profiles,
        Func<InstalledApplication, ProtectionTarget?, GestureAuthorization?> verifyAuthorization)
    {
        var match = Match(running, catalog);
        var risk = risks.Assess(running.ExecutableName ?? match?.ExecutableName, running.Aumid ?? match?.Aumid,
            match?.RiskLevel ?? RiskLevel.Unknown);
        var target = match is { IdentificationStatus: IdentificationStatus.Matched }
            && running is { InstallationStamp: { } stamp, WindowKind: not ForegroundWindowKind.ApplicationHost }
            ? new ProtectionTarget(match.AppId, running.ExecutablePath, risk, stamp) : null;
        var grant = match is not null && target is not null ? verifyAuthorization(match, target) : null;
        var status = protection.Evaluate(target, grant, match?.UserOverride is { Allowed: false });
        return new(match, risk, status, grant, profiles.Resolve(match?.AppId, status));
    }

    public static InstalledApplication? Match(RunningApplication running, IEnumerable<InstalledApplication> catalog)
    {
        if (!string.IsNullOrWhiteSpace(running.Aumid))
        {
            var aumid = catalog.FirstOrDefault(item => item.AppId == ApplicationIdentity.ForAumid(running.Aumid));
            if (aumid is not null || running.PackageFamilyName is not null || running.WindowKind == ForegroundWindowKind.ApplicationHost)
                return aumid;
        }

        if (running.PackageFamilyName is not null || running.WindowKind == ForegroundWindowKind.ApplicationHost)
            return null;

        var path = ApplicationIdentity.NormalizeExecutable(running.ExecutablePath);
        if (path is null)
            return null;

        var matches = catalog.Where(item => item.IdentificationStatus == IdentificationStatus.Matched
                && string.Equals(path, item.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return matches.Length == 1 && matches[0].PackageFamilyName is null ? matches[0] : null;
    }

    internal static RunningApplication? InspectWindow(nint window)
    {
        if (window == 0 || Native.GetWindowThreadProcessId(window, out var ownerId) == 0 || ownerId == 0)
            return null;

        var owner = ReadProcess(ownerId);
        var className = new StringBuilder(256);
        Native.GetClassName(window, className, className.Capacity);
        var windowClass = className.ToString();
        var windowAumid = Native.ReadWindowAumid(window);
        if (string.IsNullOrWhiteSpace(windowAumid))
            windowAumid = null;
        var isHost = IsApplicationHost(owner.Name, windowClass);
        var effective = owner;
        if (isHost)
        {
            var children = new HashSet<uint>();
            Native.EnumChildWindows(window, (child, _) =>
            {
                if (Native.GetWindowThreadProcessId(child, out var childId) != 0 && childId != ownerId)
                    children.Add(childId);
                return true;
            }, 0);
            var candidates = children.Select(ReadProcess).Where(child => child.Aumid is not null
                && (windowAumid is null || string.Equals(child.Aumid, windowAumid, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(child => child.Aumid!, StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            effective = candidates.Length == 1 ? candidates[0].First() : new ProcessIdentity(ownerId, null, null, null, null);
        }

        var aumid = windowAumid ?? effective.Aumid;
        var family = FamilyFromAumid(aumid) ?? effective.Family;
        var shell = Native.GetShellWindow();
        var kind = ClassifyWindow(effective.Name ?? owner.Name, windowClass, isHost, family, window == shell);
        if (kind == ForegroundWindowKind.ExplorerDesktop && !BelongsToDesktopShell(window, ownerId, shell))
            kind = ForegroundWindowKind.DesktopProgram;
        if (Native.GetWindowThreadProcessId(window, out var currentOwner) == 0 || currentOwner != ownerId)
            return null;

        return new(window, (int)ownerId, (int)effective.Pid, windowClass, kind, effective.Name ?? owner.Name,
            effective.Path, aumid, family);
    }

    internal static bool IsDesktopSurface(nint window, uint shellOwner)
    {
        var shell = Native.GetShellWindow();
        if (!BelongsToDesktopShell(window, shellOwner, shell))
            return false;
        if (window == shell)
            return true;

        var windowClass = new StringBuilder(32);
        Native.GetClassName(window, windowClass, windowClass.Capacity);
        return windowClass.ToString() is "Progman" or "WorkerW";
    }

    private static bool BelongsToDesktopShell(nint window, uint owner, nint shell) =>
        window != 0 && shell != 0
        && Native.GetWindowThreadProcessId(shell, out var shellOwner) != 0 && shellOwner == owner
        && Native.GetWindowThreadProcessId(window, out var windowOwner) != 0 && windowOwner == owner;

    public static ForegroundWindowKind ClassifyWindow(string? executableName, string windowClass, bool isHost,
        string? packageFamily, bool isShellWindow = false)
    {
        if (isShellWindow || (IsExplorer(executableName) && windowClass is "Progman" or "WorkerW"))
            return ForegroundWindowKind.ExplorerDesktop;
        if (IsExplorer(executableName) && windowClass is "CabinetWClass" or "ExploreWClass")
            return ForegroundWindowKind.ExplorerFolder;
        if (packageFamily is not null)
            return ForegroundWindowKind.PackagedApp;
        return isHost ? ForegroundWindowKind.ApplicationHost : ForegroundWindowKind.DesktopProgram;
    }

    private static bool IsExplorer(string? name) => string.Equals(name, "explorer.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsApplicationHost(string? name, string windowClass) =>
        windowClass == "ApplicationFrameWindow" || name?.ToUpperInvariant() is
            "APPLICATIONFRAMEHOST.EXE" or "WWAHOST.EXE" or "RUNTIMEBROKER.EXE" or "BACKGROUNDTASKHOST.EXE"
            or "DLLHOST.EXE" or "RUNDLL32.EXE" or "SVCHOST.EXE";

    internal static string? FamilyFromAumid(string? aumid)
    {
        var divider = aumid?.IndexOf('!') ?? -1;
        if (divider <= 0)
            return null;
        var family = aumid![..divider];
        var suffix = family.LastIndexOf('_');
        return suffix > 0 && family.Length - suffix == 14 && family[(suffix + 1)..].All(char.IsAsciiLetterOrDigit)
            ? family : null;
    }

    private sealed record ProcessIdentity(uint Pid, string? Name, string? Path, string? Aumid, string? Family);

    private static ProcessIdentity ReadProcess(uint pid)
    {
        using var process = WindowsProcessQuery.OpenForQuery(pid);
        if (!process.IsInvalid)
        {
            var executable = new StringBuilder(32768);
            var length = executable.Capacity;
            var path = Native.QueryFullProcessImageName(process, 0, executable, ref length)
                ? ApplicationIdentity.NormalizeExecutable(executable.ToString()) : null;
            var aumid = Native.ReadIdentity(process, Native.GetApplicationUserModelId);
            var family = Native.ReadIdentity(process, Native.GetPackageFamilyName);
            return new(pid, path is not null ? Path.GetFileName(path) : TryProcessName(pid), path, aumid, family);
        }

        return new(pid, TryProcessName(pid), null, null, null);
    }

    private static string? TryProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return string.IsNullOrWhiteSpace(name) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static class Native
    {
        public delegate bool WindowCallback(nint window, nint data);
        public delegate int ProcessIdentityGetter(SafeProcessHandle process, ref uint length, StringBuilder? value);

        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern nint GetShellWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(nint window, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(nint window, StringBuilder className, int capacity);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(nint window, WindowCallback callback, nint data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder? value);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder? value);

        public static string? ReadIdentity(SafeProcessHandle process, ProcessIdentityGetter getValue)
        {
            const int insufficientBuffer = 122;
            uint length = 0;
            if (getValue(process, ref length, null) != insufficientBuffer || length is 0 or > 32768)
                return null;
            var value = new StringBuilder((int)length);
            return getValue(process, ref length, value) == 0 && value.Length > 0 ? value.ToString() : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct PropertyKey(Guid formatId, uint propertyId)
        {
            private readonly Guid _formatId = formatId;
            private readonly uint _propertyId = propertyId;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropertyValue
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public nint Pointer;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(in PropertyKey key, out PropertyValue value);
        }

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHGetPropertyStoreForWindow(nint window, in Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int PropVariantClear(ref PropertyValue value);

        public static string? ReadWindowAumid(nint window)
        {
            var interfaceId = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(window, in interfaceId, out var store) < 0 || store is null)
                return null;

            try
            {
                var appId = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
                var result = store.GetValue(in appId, out var value);
                try
                {
                    const ushort unicodeString = 31;
                    return result >= 0 && value.Type == unicodeString ? Marshal.PtrToStringUni(value.Pointer) : null;
                }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
    }
}
