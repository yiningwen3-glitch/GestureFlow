using System.IO;
using System.Security;
using Microsoft.Win32;

namespace GestureFlow.Desktop.Applications;

public sealed class RegistryApplicationSource : IApplicationDiscoverySource
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<DiscoveryBatch> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private static DiscoveryBatch Scan(CancellationToken cancellationToken)
    {
        var roots = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry32, DiscoverySource.UserRegistry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64, DiscoverySource.UserRegistry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32, DiscoverySource.SystemRegistry32),
            (RegistryHive.LocalMachine, RegistryView.Registry64, DiscoverySource.SystemRegistry64)
        };
        var rows = new List<ApplicationObservation>();
        var warnings = new List<string>();
        foreach (var (hive, view, source) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null)
                    continue;

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var key = uninstall.OpenSubKey(name);
                        var row = Read(key, name, hive, view, source);
                        if (row is not null)
                            rows.Add(row);
                    }
                    catch (Exception error) when (IsReadError(error))
                    {
                        warnings.Add($"部分 {hive}/{view} 安装记录无法读取。");
                    }
                }
            }
            catch (Exception error) when (IsReadError(error))
            {
                warnings.Add($"无法读取 {hive}/{view} 已安装程序。");
            }
        }

        return new(rows, warnings);
    }

    private static ApplicationObservation? Read(RegistryKey? key, string keyName, RegistryHive hive,
        RegistryView view, DiscoverySource source)
    {
        if (key is null || key.GetValue("SystemComponent") is 1 || key.GetValue("DisplayName") is not string name
            || string.IsNullOrWhiteSpace(name))
            return null;

        var location = ApplicationIdentity.NormalizePath(key.GetValue("InstallLocation") as string);
        var exe = ApplicationIdentity.NormalizeExecutable(location);
        var icon = ParseIcon(key.GetValue("DisplayIcon") as string);
        var publisher = (key.GetValue("Publisher") as string)?.Trim();
        var anchor = Guid.TryParse(keyName, out var product)
            ? $"msi:{hive}:{product:D}"
            : location is not null
                ? $"uninstall:{hive}:{keyName}:{location}:{publisher}"
                : $"uninstall:{hive}:{(hive == RegistryHive.CurrentUser ? "shared" : view.ToString())}:{keyName}";
        return new(anchor, name.Trim(), source, IsLocalFile(exe) ? exe : null,
            Icon: icon, ExecutableHint: icon?.Value, InstallDirectory: location);
    }

    internal static ApplicationIcon? ParseIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var index = 0;
        var comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value.AsSpan(comma + 1), out index))
            value = value[..comma];

        var path = ApplicationIdentity.NormalizePath(value.Trim().TrimStart('@'));
        return path is null ? null : new ApplicationIcon(path, Index: index);
    }

    internal static bool IsLocalFile(string? path) =>
        path is not null && !path.StartsWith(@"\\", StringComparison.Ordinal) && File.Exists(path);

    private static bool IsReadError(Exception error) => error is UnauthorizedAccessException or SecurityException or IOException;
}
