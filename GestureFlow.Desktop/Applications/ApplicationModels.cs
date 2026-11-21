using System.IO;
using System.Security.Cryptography;
using System.Text;
using GestureFlow.Core.Protection;

namespace GestureFlow.Desktop.Applications;

[Flags]
public enum DiscoverySource
{
    None = 0, UserRegistry32 = 1, UserRegistry64 = 2, SystemRegistry32 = 4, SystemRegistry64 = 8,
    StartMenu = 16, Appx = 32, Running = 64, Manual = 128, Desktop = 256, Office = 512
}

public enum IdentificationStatus { Matched, Pending }

public sealed record ApplicationIcon(string Value, bool IsShellItem = false, int Index = 0);

public sealed record ApplicationOverride(bool Allowed, RiskLevel RiskLevel, GestureTrigger Trigger,
    bool RequiresProtection, bool HasUserAuthorization);

public sealed record InstalledApplication(string AppId, string DisplayName, string? ExecutableName,
    string? ExecutablePath, string? PackageFamilyName, string? Aumid, ApplicationIcon? Icon,
    DiscoverySource DiscoverySource, IdentificationStatus IdentificationStatus,
    bool GestureEnabled, RiskLevel RiskLevel, ApplicationOverride? UserOverride, string? InstallationStamp = null);

public sealed record ApplicationObservation(string RecordKey, string DisplayName, DiscoverySource Source,
    string? ExecutablePath = null, string? ExecutableName = null, string? PackageFamilyName = null,
    string? Aumid = null, ApplicationIcon? Icon = null, string? ExecutableHint = null,
    string? InstallDirectory = null);

public sealed record ManualApplication(string ExecutablePath, string? AssociatedAppId = null);

public sealed record DiscoveryBatch(IReadOnlyList<ApplicationObservation> Applications, IReadOnlyList<string> Warnings);

public interface IApplicationDiscoverySource
{
    Task<DiscoveryBatch> DiscoverAsync(CancellationToken cancellationToken);
}

public static class ApplicationIdentity
{
    public static string ForPath(string path) =>
        Hash("exe", NormalizeExecutable(path) ?? throw new ArgumentException("需要完整的 .exe 路径。", nameof(path)));
    public static string ForAumid(string aumid) => Hash("aumid", aumid);
    public static string ForRecord(string recordKey) => Hash("record", recordKey);

    public static string? NormalizeExecutable(string? path)
    {
        var normalized = NormalizePath(path);
        return normalized is { Length: > 2 } && !normalized.AsSpan(2).Contains(':')
            && Path.GetExtension(normalized).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized : null;
    }

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (expanded.Any(char.IsControl) || !Path.IsPathFullyQualified(expanded))
                return null;

            if (expanded.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                expanded = @"\\" + expanded[8..];
            else if (expanded.StartsWith(@"\\?\", StringComparison.Ordinal) && expanded.Length > 6 && expanded[5] == ':')
                expanded = expanded[4..];
            else if (expanded.StartsWith(@"\\?\", StringComparison.Ordinal) || expanded.StartsWith(@"\\.\", StringComparison.Ordinal))
                return null;

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool IsManageableId(string? id) => IsId(id, "exe:") || IsId(id, "aumid:");
    public static bool IsPendingId(string? id) => IsId(id, "record:");

    private static bool IsId(string? id, string prefix) =>
        id is not null && id.StartsWith(prefix, StringComparison.Ordinal) && id.Length == prefix.Length + 64
        && id.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789ABCDEF") == -1;

    private static string Hash(string prefix, string value) =>
        $"{prefix}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))}";
}

public static class ApplicationGesturePolicy
{
    public static RiskLevel EffectiveRisk(RiskLevel baseline, ApplicationOverride? userOverride) =>
        userOverride?.RiskLevel == RiskLevel.High ? RiskLevel.High : baseline;

    public static GestureAuthorization? VerifiedAuthorization(ProtectionTarget? target, ApplicationOverride? policy,
        IReadOnlyDictionary<string, GestureAuthorization> authorizations) =>
        target is not null && policy is { Allowed: true, HasUserAuthorization: true, RequiresProtection: true }
        && policy.RiskLevel == target.Risk.Level && authorizations.TryGetValue(target.AppId, out var authorization)
        && authorization.Trigger == policy.Trigger && ProtectionPolicyService.IsCurrent(target, authorization)
            ? authorization : null;
}
