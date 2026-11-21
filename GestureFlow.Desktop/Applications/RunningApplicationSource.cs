using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GestureFlow.Desktop.Applications;

public sealed class RunningApplicationSource : IApplicationDiscoverySource
{
    public Task<DiscoveryBatch> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private static DiscoveryBatch Scan(CancellationToken cancellationToken)
    {
        var applications = new List<ApplicationObservation>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var window = process.MainWindowHandle;
                    if (window == 0)
                        continue;

                    var running = ForegroundApplicationService.InspectWindow(window);
                    if (running is null || running.WindowOwnerProcessId != process.Id)
                        continue;
                    var name = running.ExecutablePath is { } path
                        ? InstalledApplicationDiscoveryService.GetFileLabel(path)
                        : running.ExecutableName ?? running.Aumid ?? $"进程 {process.Id}";
                    applications.Add(new($"process:{process.Id}:{running.WindowClass}", name, DiscoverySource.Running,
                        running.ExecutablePath, running.ExecutableName, running.PackageFamilyName, running.Aumid));
                }
                catch (Exception error) when (error is ExternalException or InvalidOperationException or ArgumentException)
                {
                    // A process can exit or become inaccessible between enumeration and inspection.
                }
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        return new(applications, []);
    }
}
