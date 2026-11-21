using System.IO;
using System.Security;
using Microsoft.Win32;

namespace GestureFlow.Desktop.Applications;

internal sealed class OfficeApplicationSource : IApplicationDiscoverySource
{
    private static readonly (string FileName, string DisplayName)[] Applications =
    [
        ("WINWORD.EXE", "Microsoft Word"), ("EXCEL.EXE", "Microsoft Excel"),
        ("POWERPNT.EXE", "Microsoft PowerPoint"), ("OUTLOOK.EXE", "Microsoft Outlook"),
        ("ONENOTE.EXE", "Microsoft OneNote"), ("MSACCESS.EXE", "Microsoft Access"),
        ("MSPUB.EXE", "Microsoft Publisher")
    ];

    public Task<DiscoveryBatch> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private static DiscoveryBatch Scan(CancellationToken cancellationToken)
    {
        var rows = new List<ApplicationObservation>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = OfficeDirectories().ToArray();
        foreach (var (fileName, displayName) in Applications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var candidate in RegisteredPaths(fileName).Concat(directories.Select(directory => Path.Combine(directory, fileName))))
            {
                var path = ApplicationIdentity.NormalizeExecutable(candidate);
                if (RegistryApplicationSource.IsLocalFile(path)
                    && string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)
                    && paths.Add(path!))
                    rows.Add(new("office:" + path, displayName, DiscoverySource.Office, path));
            }
        }
        return new(rows, []);
    }

    private static IEnumerable<string> RegisteredPaths(string fileName)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string? path;
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var entry = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
                    path = entry?.GetValue(null) as string;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException or SecurityException)
                {
                    continue;
                }
                if (path is not null)
                    yield return path;
            }
    }

    private static IEnumerable<string> OfficeDirectories()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        foreach (var folder in folders.Where(folder => folder.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var office = Path.Combine(folder, "Microsoft Office");
            foreach (var root in new[] { office, Path.Combine(office, "root") })
            {
                if (!Directory.Exists(root))
                    continue;

                string[] directories;
                try { directories = Directory.GetDirectories(root, "Office*"); }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException) { continue; }

                foreach (var directory in directories)
                    yield return directory;
            }
        }
    }
}
