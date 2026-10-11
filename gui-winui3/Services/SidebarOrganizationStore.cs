using System.Text.Json;

namespace RuApp.Services;

internal sealed record SidebarOrganizationPreferences
{
    public Dictionary<string, string?> Groups { get; init; } = new(StringComparer.Ordinal);
    public List<string> Order { get; init; } = [];
}

/// <summary>Atomic local UI preferences, independent of durable run workspace identity.</summary>
internal sealed class SidebarOrganizationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;
    internal SidebarOrganizationPreferences Current { get; private set; }

    internal SidebarOrganizationStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru", "sidebar-organization.json");
        Current = Load();
    }

    internal bool Move(string sessionId, string? groupPath, string? beforeSessionId, IReadOnlyList<string> orderedIds)
    {
        if (!orderedIds.Contains(sessionId, StringComparer.Ordinal) || beforeSessionId == sessionId) return false;
        if (beforeSessionId is not null && !orderedIds.Contains(beforeSessionId, StringComparer.Ordinal)) return false;
        var order = orderedIds.Distinct(StringComparer.Ordinal).Where(id => id != sessionId).ToList();
        var index = beforeSessionId is null ? order.Count : order.IndexOf(beforeSessionId);
        order.Insert(index, sessionId);
        var groups = new Dictionary<string, string?>(Current.Groups, StringComparer.Ordinal);
        groups[sessionId] = groupPath is null ? null : SidebarSessionOrganization.Normalize(groupPath);
        return Save(new() { Groups = groups, Order = order });
    }

    internal bool Remove(string sessionId)
    {
        var groups = new Dictionary<string, string?>(Current.Groups, StringComparer.Ordinal);
        groups.Remove(sessionId);
        return Save(new() { Groups = groups, Order = Current.Order.Where(id => id != sessionId).ToList() });
    }

    private SidebarOrganizationPreferences Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new();
            var bytes = PreferenceFile.ReadOrNull(_filePath);
            var loaded = bytes is null ? null : JsonSerializer.Deserialize<SidebarOrganizationPreferences>(bytes, JsonOptions);
            if (loaded?.Groups is null || loaded.Order is null) return new();
            var groups = loaded.Groups.Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(pair => pair.Key,
                    pair => string.IsNullOrWhiteSpace(pair.Value) ? null : SidebarSessionOrganization.Normalize(pair.Value),
                    StringComparer.Ordinal);
            return new() { Groups = groups, Order = loaded.Order.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return new();
        }
    }

    private bool Save(SidebarOrganizationPreferences preferences)
    {
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(preferences, JsonOptions);
            if (bytes.Length > PreferenceFile.MaximumBytes) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, _filePath, overwrite: true);
            Current = preferences;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
