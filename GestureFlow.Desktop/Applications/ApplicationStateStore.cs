using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GestureFlow.Core.Protection;

namespace GestureFlow.Desktop.Applications;

public sealed class ApplicationStateStore(string directory)
{
    private sealed record PolicyData(int Version, Dictionary<string, ApplicationOverride> Applications);
    private sealed record ManualData(int Version, ManualApplication[] Applications);
    private sealed record AuthorizationData(int Version, Dictionary<string, GestureAuthorization> Authorizations);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private readonly string _directory = Path.GetFullPath(directory);
    private string PolicyPath => Path.Combine(_directory, "application-policies.json");
    private string ManualPath => Path.Combine(_directory, "manual-applications.json");
    private string AuthorizationPath => Path.Combine(_directory, "application-authorizations.dat");

    public IReadOnlyDictionary<string, ApplicationOverride> LoadPolicies() => ReadPolicies();

    public IReadOnlyList<ManualApplication> LoadManualApplications() => Array.AsReadOnly(ReadManual());

    public IReadOnlyDictionary<string, GestureAuthorization> LoadAuthorizations() => ReadAuthorizations();

    public void ResetProtection()
    {
        WriteLocked(() =>
        {
            // Revoke the protected grants first, even when the older files cannot be parsed.
            WriteAuthorizations(new(StringComparer.Ordinal));
            Write(PolicyPath, new PolicyData(1, new(StringComparer.Ordinal)));
            return true;
        });
    }

    public IReadOnlyDictionary<string, ApplicationOverride> SavePolicy(string appId, ApplicationOverride policy)
    {
        ValidatePolicy(appId, policy);
        if (policy.Allowed || policy.HasUserAuthorization)
            throw new JsonException("启用需要完成风险确认流程。");

        return WriteLocked(() =>
        {
            var policies = ReadPolicies();
            var authorizations = ReadAuthorizations();
            if (authorizations.Remove(appId))
                WriteAuthorizations(authorizations);
            policies[appId] = policy;
            Write(PolicyPath, new PolicyData(1, policies));
            return policies;
        });
    }

    public IReadOnlyDictionary<string, ApplicationOverride> EnableSideButton(ProtectionTarget target, GestureTrigger trigger)
    {
        if (trigger is not (GestureTrigger.MouseX1 or GestureTrigger.MouseX2)
            || !ApplicationIdentity.IsManageableId(target.AppId) || string.IsNullOrEmpty(target.InstallationStamp))
            throw new JsonException("无法核对应用安装标识。");

        return WriteLocked(() =>
        {
            var policies = ReadPolicies();
            var authorizations = ReadAuthorizations();
            policies[target.AppId] = new(true, RiskLevel.Compatible, trigger, true, true);
            authorizations[target.AppId] = new(target.AppId, target.ExecutablePath, target.Risk.PolicyStamp,
                trigger, ProtectionPolicyService.RequiredMeasures, target.InstallationStamp);
            Write(PolicyPath, new PolicyData(1, policies));
            WriteAuthorizations(authorizations);
            return policies;
        });
    }

    public IReadOnlyDictionary<string, ApplicationOverride> SaveConfirmedPolicy(ProtectionTarget target,
        CompletedAuthorization completed)
    {
        var authorization = completed.Data;
        if (!ApplicationIdentity.IsManageableId(target.AppId) || !ProtectionPolicyService.IsCurrent(target, authorization)
            || !completed.TryConsume())
            throw new JsonException("授权目标或风险规则已变化，请重新确认。");

        return WriteLocked(() =>
        {
            var policies = ReadPolicies();
            var authorizations = ReadAuthorizations();
            policies[target.AppId] = new(true, target.Risk.Level, authorization.Trigger, true, true);
            authorizations[target.AppId] = authorization;
            Write(PolicyPath, new PolicyData(1, policies));
            WriteAuthorizations(authorizations);
            return policies;
        });
    }

    public void AddManualApplication(ManualApplication application)
    {
        var normalized = ValidateManual(application);
        WriteLocked(() =>
        {
            var current = ReadManual();
            var updated = current.Where(entry => normalized.AssociatedAppId is not null
                ? entry.AssociatedAppId != normalized.AssociatedAppId
                : entry.AssociatedAppId is not null || !string.Equals(entry.ExecutablePath, normalized.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            Write(ManualPath, new ManualData(1, [.. updated, normalized]));
            return true;
        });
    }

    private Dictionary<string, ApplicationOverride> ReadPolicies()
    {
        var stored = Read(PolicyPath, new PolicyData(1, new Dictionary<string, ApplicationOverride>(StringComparer.Ordinal)));
        if (stored.Version != 1 || stored.Applications is null)
            throw new JsonException("应用策略文件版本或内容无效。");

        foreach (var (id, policy) in stored.Applications)
            ValidatePolicy(id, policy);
        return stored.Applications;
    }

    private ManualApplication[] ReadManual()
    {
        var stored = Read(ManualPath, new ManualData(1, []));
        if (stored.Version != 1 || stored.Applications is null)
            throw new JsonException("手动应用清单版本或内容无效。");

        return stored.Applications.Select(ValidateManual).ToArray();
    }

    private Dictionary<string, GestureAuthorization> ReadAuthorizations()
    {
        byte[] encrypted;
        try { encrypted = File.ReadAllBytes(AuthorizationPath); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(StringComparer.Ordinal);
        }

        var json = WindowsUserDataProtection.Unprotect(encrypted);
        var stored = JsonSerializer.Deserialize<AuthorizationData>(json, Options);
        if (stored is not { Version: 1, Authorizations: not null } || stored.Authorizations.Any(pair =>
            !ApplicationIdentity.IsManageableId(pair.Key) || pair.Value is null || pair.Value.AppId != pair.Key
            || string.IsNullOrWhiteSpace(pair.Value.PolicyStamp) || !Enum.IsDefined(pair.Value.Trigger)
            || pair.Value.Measures != ProtectionPolicyService.RequiredMeasures))
            throw new JsonException("应用授权记录无效。");
        return stored.Authorizations;
    }

    private void WriteAuthorizations(Dictionary<string, GestureAuthorization> authorizations)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new AuthorizationData(1, authorizations), Options);
        WriteBytes(AuthorizationPath, WindowsUserDataProtection.Protect(json));
    }

    private static void ValidatePolicy(string appId, ApplicationOverride? policy)
    {
        if (!ApplicationIdentity.IsManageableId(appId) || policy is null
            || !Enum.IsDefined(policy.RiskLevel) || !Enum.IsDefined(policy.Trigger)
            || (policy.Allowed && policy.RiskLevel != RiskLevel.Compatible && !policy.HasUserAuthorization)
            || (policy.Allowed && policy.RiskLevel == RiskLevel.High && !policy.RequiresProtection))
            throw new JsonException("应用标识或授权策略无效。");
    }

    private static ManualApplication ValidateManual(ManualApplication? application)
    {
        var path = ApplicationIdentity.NormalizeExecutable(application?.ExecutablePath);
        if (application is null || path is null
            || (application.AssociatedAppId is not null && !ApplicationIdentity.IsPendingId(application.AssociatedAppId)))
            throw new JsonException("手动应用路径或关联标识无效。");

        return application with { ExecutablePath = path };
    }

    private static T Read<T>(string path, T fallback) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
                ?? throw new JsonException("应用数据文件不能为空。");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return fallback;
        }
    }

    private T WriteLocked<T>(Func<T> update)
    {
        var lockName = @"Local\GestureFlow." + ApplicationIdentity.ForRecord(_directory)[7..];
        using var mutex = new Mutex(false, lockName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new IOException("另一个 GestureFlow 窗口正在保存应用数据。");
            return update();
        }
        finally
        {
            if (acquired)
                mutex.ReleaseMutex();
        }
    }

    private static void Write<T>(string path, T value) =>
        WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Options));

    private static void WriteBytes(string path, byte[] value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, value);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
