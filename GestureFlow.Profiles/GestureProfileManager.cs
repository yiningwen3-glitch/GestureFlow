using System.Security;
using System.Text.Json;
using GestureFlow.Core;
using GestureFlow.Core.Protection;

namespace GestureFlow.Profiles;

public sealed class GestureProfileManager
{
    private sealed record Catalog(int Version, JsonElement[] Profiles,
        Dictionary<string, JsonElement> Applications, JsonElement? Global = null);

    public const string FileName = "gesture-profiles.json";
    public static ManagedGestureProfile SystemDefault { get; } = new(
        new Guid("bac6718e-f422-414c-a177-68df2a182468"), "系统默认", GestureProfileScope.Global,
        null, 4, DefaultGestureProfiles.Create(GestureMode.Four).Bindings, true,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private readonly object _gate = new();
    private readonly string? _path;
    private Dictionary<Guid, ManagedGestureProfile> _profiles = [];
    private Dictionary<string, Guid> _applications = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _global;
    private string? _damagedContents;

    public GestureProfileManager() { }

    private GestureProfileManager(string directory) => _path = Path.Combine(directory, FileName);

    public string? LoadWarning { get; private set; }

    public static GestureProfileManager Open(string directory)
    {
        var manager = new GestureProfileManager(directory);
        string? contents = null;
        try
        {
            contents = File.ReadAllText(manager._path!);
            var catalog = JsonSerializer.Deserialize<Catalog>(contents, GestureProfileJson.Options);
            if (catalog is not { Version: 1, Profiles: not null, Applications: not null })
                throw new JsonException("配置方案目录格式无效。");

            if (catalog.Global is { } selected)
            {
                if (TryReadId(selected, out var id))
                    manager._global = id;
                else
                    manager.MarkDamaged(contents);
            }
            foreach (var (appId, selectedId) in catalog.Applications)
            {
                if (TryReadId(selectedId, out var id))
                    manager._applications[appId] = id;
                else
                    manager.MarkDamaged(contents);
            }
            var duplicateIds = new HashSet<Guid>();
            foreach (var entry in catalog.Profiles)
            {
                try
                {
                    var profile = ReadProfile(entry);
                    if (duplicateIds.Contains(profile.ProfileId) || !manager._profiles.TryAdd(profile.ProfileId, profile))
                    {
                        manager._profiles.Remove(profile.ProfileId);
                        duplicateIds.Add(profile.ProfileId);
                        throw new JsonException("配置方案 ID 重复。");
                    }
                }
                catch (Exception error) when (error is JsonException or ProfileValidationException)
                {
                    manager.MarkDamaged(contents);
                }
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            manager.MarkDamaged(contents);
        }
        return manager;
    }

    private static bool TryReadId(JsonElement element, out Guid id)
    {
        id = Guid.Empty;
        return element.ValueKind == JsonValueKind.String && element.TryGetGuid(out id) && id != Guid.Empty;
    }

    private void MarkDamaged(string? contents)
    {
        _damagedContents = contents;
        LoadWarning = "部分配置方案无法读取，已使用可用方案。再次保存时会先备份损坏的文件。";
    }

    public IReadOnlyList<ManagedGestureProfile> Profiles
    {
        get { lock (_gate) return [SystemDefault, .. _profiles.Values.OrderBy(profile => profile.Name)]; }
    }

    public GestureProfileManager CreatePreview()
    {
        lock (_gate)
            return new()
            {
                _profiles = new(_profiles),
                _global = _global,
                _applications = CopySelections(),
                LoadWarning = LoadWarning
            };
    }

    public ManagedGestureProfile? Selected(string? appId = null)
    {
        lock (_gate)
            return appId is null ? Find(_global ?? SystemDefault.ProfileId, null)
                : Find(_applications.GetValueOrDefault(appId), appId);
    }

    public ManagedGestureProfile? Resolve(string? appId, ProtectionStatus protection)
    {
        return protection == ProtectionStatus.Ready ? ConfiguredFor(appId) : null;
    }

    public ManagedGestureProfile ConfiguredFor(string? appId)
    {
        lock (_gate)
            return Selected(appId) ?? Selected() ?? SystemDefault;
    }

    private ManagedGestureProfile? Find(Guid id, string? appId)
    {
        if (id == SystemDefault.ProfileId)
            return appId is null ? SystemDefault : null;
        return _profiles.TryGetValue(id, out var profile)
            && string.Equals(profile.AppId, appId, StringComparison.OrdinalIgnoreCase) ? profile : null;
    }

    public ManagedGestureProfile Create(string name, string? appId = null, GestureProfile? configuration = null,
        bool select = false)
    {
        configuration ??= SystemDefault.Configuration;
        var now = DateTimeOffset.UtcNow;
        return Add(new(Guid.NewGuid(), name.Trim(), appId is null ? GestureProfileScope.Global : GestureProfileScope.Application,
            appId, (int)configuration.Mode, configuration.Bindings, false, now, now), select);
    }

    public ManagedGestureProfile Copy(Guid id, string name)
    {
        lock (_gate)
            return Copy(id, name, Get(id).AppId);
    }

    public ManagedGestureProfile Copy(Guid id, string name, string? appId)
    {
        lock (_gate)
            return Create(name, appId, Get(id).Configuration);
    }

    public ManagedGestureProfile Rename(Guid id, string name) =>
        Update(id, profile => profile with { Name = name.Trim() });

    public ManagedGestureProfile Edit(Guid id, GestureProfile configuration, bool select = false) =>
        Update(id, profile => profile with { SectorCount = (int)configuration.Mode, Gestures = configuration.Bindings }, select);

    private ManagedGestureProfile Update(Guid id, Func<ManagedGestureProfile, ManagedGestureProfile> change, bool select = false)
    {
        lock (_gate)
        {
            var profile = Freeze(change(Editable(id)) with { UpdatedAt = DateTimeOffset.UtcNow });
            var next = new Dictionary<Guid, ManagedGestureProfile>(_profiles) { [id] = profile };
            SaveWithSelection(next, profile, select);
            return profile;
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            Editable(id);
            var next = new Dictionary<Guid, ManagedGestureProfile>(_profiles);
            next.Remove(id);
            var applications = CopySelections();
            foreach (var key in _applications.Where(pair => pair.Value == id).Select(pair => pair.Key))
                applications.Remove(key);
            Save(next, _global == id ? null : _global, applications);
        }
    }

    public void Select(Guid? id, string? appId = null)
    {
        lock (_gate)
        {
            if (id is { } value && Find(value, appId) is null)
                throw new InvalidOperationException("方案不存在或不属于当前范围。");
            var applications = CopySelections();
            if (appId is not null)
                SetApplicationSelection(applications, appId, id);
            Save(_profiles, appId is null ? id : _global, applications);
        }
    }

    public string Export(Guid id)
    {
        lock (_gate)
            return JsonSerializer.Serialize(Get(id), GestureProfileJson.Options);
    }

    public ManagedGestureProfile Import(string json, string? appId = null, bool select = false)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("mode", out _))
            return Create("导入的笔势方案", appId, GestureProfileJson.Deserialize(json), select);

        var imported = ReadProfile(document.RootElement, allowBuiltIn: true);
        lock (_gate)
        {
            var collision = imported.ProfileId == SystemDefault.ProfileId || _profiles.ContainsKey(imported.ProfileId);
            return Add(imported with
            {
                ProfileId = collision ? Guid.NewGuid() : imported.ProfileId,
                Scope = appId is null ? GestureProfileScope.Global : GestureProfileScope.Application,
                AppId = appId,
                ReadOnly = false
            }, select);
        }
    }

    private ManagedGestureProfile Add(ManagedGestureProfile profile, bool select)
    {
        profile = Freeze(profile);
        lock (_gate)
        {
            var next = new Dictionary<Guid, ManagedGestureProfile>(_profiles) { [profile.ProfileId] = profile };
            SaveWithSelection(next, profile, select);
            return profile;
        }
    }

    private void SaveWithSelection(Dictionary<Guid, ManagedGestureProfile> next, ManagedGestureProfile profile, bool select)
    {
        var applications = CopySelections();
        if (select && profile.AppId is { } appId)
            SetApplicationSelection(applications, appId, profile.ProfileId);
        Save(next, select && profile.AppId is null ? profile.ProfileId : _global, applications);
    }

    private static ManagedGestureProfile ReadProfile(JsonElement json, bool allowBuiltIn = false)
    {
        var profile = json.Deserialize<ManagedGestureProfile>(GestureProfileJson.Options)
            ?? throw new JsonException("配置方案应为 JSON 对象。");
        if (!allowBuiltIn && (profile.ReadOnly || profile.ProfileId == SystemDefault.ProfileId))
            throw new JsonException("配置文件不能替换系统默认方案。");
        return Freeze(profile);
    }

    private static ManagedGestureProfile Freeze(ManagedGestureProfile profile)
    {
        if (profile.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name)
            || profile.CreatedAt == default || profile.UpdatedAt < profile.CreatedAt
            || !Enum.IsDefined(profile.Scope)
            || (profile.Scope == GestureProfileScope.Global ? profile.AppId is not null : string.IsNullOrWhiteSpace(profile.AppId)))
            throw new JsonException("配置方案的标识、名称、时间或范围无效。");

        var configuration = new GestureProfile((GestureMode)profile.SectorCount, profile.Gestures, []);
        var errors = GestureProfileValidator.Validate(configuration);
        if (errors.Count != 0)
            throw new ProfileValidationException(errors);
        var gestures = Array.AsReadOnly(profile.Gestures.ToArray());
        return profile with { Gestures = gestures, Configuration = configuration with { Bindings = gestures } };
    }

    private ManagedGestureProfile Get(Guid id) => id == SystemDefault.ProfileId ? SystemDefault
        : _profiles.GetValueOrDefault(id) ?? throw new InvalidOperationException("方案不存在。");

    private ManagedGestureProfile Editable(Guid id) => Get(id) is { ReadOnly: false } profile
        ? profile : throw new InvalidOperationException("系统默认方案为只读，请先复制。");

    private Dictionary<string, Guid> CopySelections() => new(_applications, StringComparer.OrdinalIgnoreCase);

    private static void SetApplicationSelection(Dictionary<string, Guid> selections, string appId, Guid? id)
    {
        if (id is { } selected)
            selections[appId] = selected;
        else
            selections.Remove(appId);
    }

    private void Save(Dictionary<Guid, ManagedGestureProfile> profiles, Guid? global, Dictionary<string, Guid> applications)
    {
        if (_path is not null)
        {
            if (LoadWarning is not null)
            {
                // A file that could not be read must also be recoverable before replacing it.
                var original = _damagedContents ?? File.ReadAllText(_path);
                AtomicWrite($"{_path}.{Guid.NewGuid():N}.recovery", original);
            }
            var entries = profiles.Values.Select(profile => JsonSerializer.SerializeToElement(profile, GestureProfileJson.Options)).ToArray();
            var selections = applications.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value));
            var catalog = new Catalog(1, entries, selections, global is { } id ? JsonSerializer.SerializeToElement(id) : null);
            AtomicWrite(_path, JsonSerializer.Serialize(catalog, GestureProfileJson.Options));
        }
        _profiles = profiles;
        _global = global;
        _applications = applications;
        LoadWarning = _damagedContents = null;
    }

    public static void AtomicWrite(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
