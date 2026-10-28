using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Protocol;

namespace RuAgent.Broker.Skills;

public sealed partial class SkillCatalog
{
    private static readonly ConcurrentDictionary<string, object> PreferenceGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PreferenceGateMapLock = new();
    private static readonly object OverflowPreferenceGate = new();

    public SkillPreferenceResult SetEnabled(string id, bool enabled) => UpdatePreference(id, prefs => prefs.Enabled[id] = enabled);
    public SkillPreferenceResult SetImplicit(string id, bool enabled) => UpdatePreference(id, prefs => prefs.Implicit[id] = enabled);

    public SkillPreferenceResult AddRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return Failure("invalid_root", "Root is required.");
        try { root = Path.GetFullPath(root); } catch (ArgumentException) { return Failure("invalid_root", "Root path is invalid."); }
        if (!Directory.Exists(root)) return Failure("missing_root", "Root directory does not exist.");
        return WithPreferenceLock(() =>
        {
            var preferences = ReadPreferences();
            if (!_baseRoots.Contains(root, StringComparer.OrdinalIgnoreCase)
                && !preferences.Roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                var allRoots = _baseRoots.Concat(preferences.Roots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (allRoots.Length >= MaxRoots) return Failure("root_limit", "The skill root limit has been reached.");
                preferences.Roots.Add(root);
            }
            var saved = Save(preferences);
            if (saved.Success) RefreshRoots();
            return saved;
        });
    }

    public SkillPreferenceResult RemoveRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return Failure("invalid_root", "Root is required.");
        try { root = Path.GetFullPath(root); } catch (ArgumentException) { return Failure("invalid_root", "Root path is invalid."); }
        return WithPreferenceLock(() =>
        {
            var preferences = ReadPreferences();
            preferences.Roots.RemoveAll(value => string.Equals(value, root, StringComparison.OrdinalIgnoreCase));
            var saved = Save(preferences);
            if (saved.Success) RefreshRoots();
            return saved;
        });
    }

    private SkillPreferenceResult UpdatePreference(string id, Action<PreferenceData> update)
    {
        if (!SkillIdentity.IsValid(id)) return Failure("invalid_skill_id", "Skill ID is invalid.");
        return WithPreferenceLock(() =>
        {
            var preferences = ReadPreferences();
            update(preferences);
            return Save(preferences);
        });
    }

    private SkillPreferenceResult WithPreferenceLock(Func<SkillPreferenceResult> update)
    {
        lock (GetPreferenceGate())
        {
            using var mutex = new Mutex(false, PreferenceMutexName());
            var acquired = false;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return Failure("preferences_busy", "Skill preferences are being updated; retry shortly.");
            try { return update(); }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private string PreferenceMutexName()
    {
        var path = OperatingSystem.IsWindows() ? _preferencesPath.ToUpperInvariant() : _preferencesPath;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        return "Ru.SkillPreferences." + digest;
    }

    private void RefreshRoots()
    {
        var preferences = ReadPreferences();
        var desired = _baseRoots.ToList();
        desired.AddRange(preferences.Roots.Where(root => !desired.Contains(root, StringComparer.OrdinalIgnoreCase)).Take(Math.Max(0, MaxRoots - desired.Count)));
        lock (_rootsGate)
        {
            if (_roots.SequenceEqual(desired, StringComparer.OrdinalIgnoreCase)) return;
            var previousKey = _cacheKey;
            _roots.Clear();
            _roots.AddRange(desired);
            _cacheKey = BuildCacheKey(_roots);
            SharedCache.TryRemove(previousKey, out _);
        }
    }

    private object GetPreferenceGate()
    {
        if (PreferenceGates.TryGetValue(_preferencesPath, out var gate)) return gate;
        lock (PreferenceGateMapLock)
        {
            if (PreferenceGates.TryGetValue(_preferencesPath, out gate)) return gate;
            if (PreferenceGates.Count >= 64) return OverflowPreferenceGate;
            gate = new object();
            PreferenceGates[_preferencesPath] = gate;
            return gate;
        }
    }

    private PreferenceData ReadPreferences()
    {
        var store = ReadPreferenceStore();
        if (store.Workspaces is not null && store.Workspaces.TryGetValue(WorkspaceKey(), out var preferences)) return Normalize(preferences);
        return new();
    }

    private SkillPreferenceResult Save(PreferenceData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath)!);
            var store = ReadPreferenceStore();
            store.Workspaces ??= new(StringComparer.Ordinal);
            store.Workspaces[WorkspaceKey()] = Normalize(data);
            var json = JsonSerializer.Serialize(store);
            if (Encoding.UTF8.GetByteCount(json) > 64 * 1024) return Failure("preferences_oversize", "Preferences exceed the storage limit.");
            var temp = _preferencesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false, true));
            File.Move(temp, _preferencesPath, true);
            string[] roots;
            lock (_rootsGate) roots = _roots.ToArray();
            SharedCache.TryRemove(BuildCacheKey(roots), out _);
            return new(true, "ok", "Skill preferences saved.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure("preferences_write_failed", "Skill preferences could not be saved.");
        }
    }

    private static SkillPreferenceResult Failure(string code, string message) => new(false, code, message);

    private PreferenceStore ReadPreferenceStore()
    {
        try
        {
            using var stream = new FileStream(_preferencesPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            var bytes = new byte[64 * 1024 + 1]; var count = 0;
            while (count < bytes.Length) { var read = stream.Read(bytes, count, bytes.Length - count); if (read == 0) break; count += read; }
            return count > 64 * 1024 ? new() : JsonSerializer.Deserialize<PreferenceStore>(bytes.AsSpan(0, count)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }

    private string WorkspaceKey()
    {
        var normalized = OperatingSystem.IsWindows() ? _workspaceRoot.ToUpperInvariant() : _workspaceRoot;
        return Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(normalized))).ToLowerInvariant();
    }

    private string BuildCacheKey(IReadOnlyList<string> roots) => string.Join("|", _workspaceRoot, _preferencesPath, string.Join(";", roots));

    private static PreferenceData Normalize(PreferenceData? data)
    {
        data ??= new PreferenceData();
        data.Roots ??= []; data.Enabled ??= new(StringComparer.Ordinal); data.Implicit ??= new(StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var root in data.Roots.Take(256))
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try { var full = Path.GetFullPath(root); if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
            if (roots.Count >= MaxRoots) break;
        }
        data.Roots = roots;
        data.Enabled = data.Enabled.Take(256).Where(pair => SkillIdentity.IsValid(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        data.Implicit = data.Implicit.Take(256).Where(pair => SkillIdentity.IsValid(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return data;
    }

    private sealed class PreferenceStore { public Dictionary<string, PreferenceData> Workspaces { get; set; } = new(StringComparer.Ordinal); }
    private sealed class PreferenceData { public List<string> Roots { get; set; } = []; public Dictionary<string, bool> Enabled { get; set; } = new(StringComparer.Ordinal); public Dictionary<string, bool> Implicit { get; set; } = new(StringComparer.Ordinal); }
}
