using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using GestureFlow.Core.Protection;

namespace GestureFlow.Desktop.Applications;

public sealed record ApplicationCatalog(IReadOnlyList<InstalledApplication> Applications, IReadOnlyList<string> Warnings);

public sealed class InstalledApplicationDiscoveryService(
    IReadOnlyList<IApplicationDiscoverySource> sources,
    Func<string, bool> executableExists,
    Func<string, string?, string?, RiskLevel>? classifyRisk = null,
    Func<InstalledApplication, bool>? isAuthorized = null,
    Func<string, string?>? readInstallationStamp = null)
{
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    public async Task<ApplicationCatalog> DiscoverAsync(IReadOnlyList<ManualApplication> manualApplications,
        IReadOnlyDictionary<string, ApplicationOverride> overrides, CancellationToken cancellationToken = default)
    {
        var batches = await Task.WhenAll(sources.Select(source => ReadSourceAsync(source, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Merge(batches.SelectMany(batch => batch.Applications), manualApplications, overrides,
            batches.SelectMany(batch => batch.Warnings).Distinct().ToArray(), cancellationToken);
    }

    private static async Task<DiscoveryBatch> ReadSourceAsync(IApplicationDiscoverySource source, CancellationToken cancellationToken)
    {
        try
        {
            return await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or ExternalException or InvalidOperationException)
        {
            return new([], ["部分应用来源暂时无法读取。"]);
        }
    }

    private ApplicationCatalog Merge(IEnumerable<ApplicationObservation> observations, IReadOnlyList<ManualApplication> manual,
        IReadOnlyDictionary<string, ApplicationOverride> overrides, IReadOnlyList<string> warnings, CancellationToken cancellationToken)
    {
        var byAssociation = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new List<ApplicationObservation>();
        foreach (var item in manual)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ApplicationIdentity.NormalizeExecutable(item.ExecutablePath);
            if (path is null || !executableExists(path))
                continue;

            rows.Add(new(path, GetFileLabel(path), DiscoverySource.Manual, path));
            if (item.AssociatedAppId is { } pendingId)
                byAssociation[pendingId] = path;
        }

        foreach (var observation in observations)
        {
            var path = ApplicationIdentity.NormalizeExecutable(observation.ExecutablePath);
            var aumid = BlankToNull(observation.Aumid);
            if (path is null && aumid is null && observation.PackageFamilyName is null
                && byAssociation.TryGetValue(ApplicationIdentity.ForRecord(observation.RecordKey), out var associated))
                path = associated;
            rows.Add(observation with { ExecutablePath = path, Aumid = aumid });
        }

        var confirmedPaths = rows.Select(row => row.ExecutablePath).OfType<string>().ToHashSet(IgnoreCase);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var hint = ApplicationIdentity.NormalizeExecutable(row.ExecutableHint);
            var directory = ApplicationIdentity.NormalizePath(row.InstallDirectory);
            if (row.ExecutablePath is null && row.Aumid is null && row.PackageFamilyName is null
                && hint is not null && directory is not null
                && confirmedPaths.Contains(hint) && IgnoreCase.Equals(Path.GetDirectoryName(hint), directory)
                && !IsInstaller(Path.GetFileNameWithoutExtension(hint)))
                rows[index] = row with { ExecutablePath = hint };
        }

        var aumidsByPath = rows.Where(row => row.ExecutablePath is not null && row.Aumid is not null)
            .ToLookup(row => row.ExecutablePath!, row => row.Aumid!, IgnoreCase);
        var keyed = rows.Select(row => (Row: row, Id: GroupKey(row, aumidsByPath))).ToArray();
        var identifiedRecords = keyed.Where(entry => ApplicationIdentity.IsManageableId(entry.Id))
            .ToLookup(entry => entry.Row.RecordKey, entry => entry.Id, IgnoreCase);
        var groups = keyed.GroupBy(entry =>
        {
            if (entry.Row.ExecutablePath is not null || !ApplicationIdentity.IsPendingId(entry.Id))
                return entry.Id;

            var candidates = identifiedRecords[entry.Row.RecordKey].Distinct(StringComparer.Ordinal).Take(2).ToArray();
            return candidates.Length == 1 ? candidates[0] : entry.Id;
        }, entry => entry.Row, StringComparer.Ordinal);
        var applications = groups.Select(group =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Build(group.Key, group, overrides);
        })
            .OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.AppId, StringComparer.Ordinal).ToArray();
        return new(applications, warnings);
    }

    private static string GroupKey(ApplicationObservation row, ILookup<string, string> aumidsByPath)
    {
        if (row.Aumid is { } aumid)
            return ApplicationIdentity.ForAumid(aumid);

        if (row.PackageFamilyName is not null)
            return ApplicationIdentity.ForRecord(row.RecordKey);

        if (row.ExecutablePath is { } path)
        {
            var matches = aumidsByPath[path].Distinct(IgnoreCase).Take(2).ToArray();
            if (matches.Length == 1)
                return ApplicationIdentity.ForAumid(matches[0]);
            if (matches.Length == 0)
                return ApplicationIdentity.ForPath(path);
            return ApplicationIdentity.ForRecord("ambiguous:" + path);
        }

        return ApplicationIdentity.ForRecord(row.RecordKey);
    }

    private InstalledApplication Build(string id, IEnumerable<ApplicationObservation> rows,
        IReadOnlyDictionary<string, ApplicationOverride> overrides)
    {
        var ordered = rows.OrderBy(row => DisplayPriority(row.Source)).ThenBy(row => row.RecordKey, IgnoreCase).ToArray();
        var aumid = ordered.Select(row => row.Aumid).OfType<string>().FirstOrDefault();
        var family = ordered.Select(row => row.PackageFamilyName).OfType<string>().FirstOrDefault();
        var path = ordered.Select(row => row.ExecutablePath).OfType<string>().FirstOrDefault();
        var executableName = path is null ? ordered.Select(row => row.ExecutableName).OfType<string>().FirstOrDefault() : Path.GetFileName(path);
        var name = ordered.Select(row => row.DisplayName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? executableName ?? aumid ?? "未命名应用";
        var icon = ordered.Select(row => row.Icon).OfType<ApplicationIcon>().FirstOrDefault()
            ?? (path is null ? null : new ApplicationIcon(path));
        var sources = ordered.Aggregate(DiscoverySource.None, (value, row) => value | row.Source);
        var identification = ApplicationIdentity.IsManageableId(id) ? IdentificationStatus.Matched : IdentificationStatus.Pending;
        overrides.TryGetValue(id, out var userOverride);
        var baseline = classifyRisk?.Invoke(id, executableName, aumid) ?? RiskLevel.Unknown;
        var application = new InstalledApplication(id, name, executableName, path, family, aumid, icon, sources, identification,
            false, ApplicationGesturePolicy.EffectiveRisk(baseline, userOverride), userOverride,
            identification == IdentificationStatus.Matched && path is not null ? readInstallationStamp?.Invoke(path) : null);
        return application with { GestureEnabled = isAuthorized?.Invoke(application) == true };
    }

    private static bool IsInstaller(string fileName) =>
        fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("setup", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("update", StringComparison.OrdinalIgnoreCase);

    private static int DisplayPriority(DiscoverySource source) => source switch
    {
        DiscoverySource.Desktop => -2,
        DiscoverySource.Office => -1,
        DiscoverySource.StartMenu => 0,
        DiscoverySource.Appx => 1,
        DiscoverySource.Manual => 3,
        DiscoverySource.Running => 4,
        _ => 2
    };

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string GetFileLabel(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return BlankToNull(version.FileDescription) ?? BlankToNull(version.ProductName) ?? Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return Path.GetFileNameWithoutExtension(path);
        }
    }
}
