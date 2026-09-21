using System.Text.Json;

namespace RuApp.Services;

/// <summary>Persists sidebar workspace expand state as a JSON flag map.</summary>
public sealed class WorkspaceGroupPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public WorkspaceGroupPreferenceStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "workspace-groups.json");
    }

    public bool IsExpanded(string workspaceKey, bool defaultExpanded)
    {
        return Load().TryGetValue(workspaceKey, out var expanded) ? expanded : defaultExpanded;
    }

    public bool SetExpanded(string workspaceKey, bool expanded)
    {
        var flags = Load();
        flags[workspaceKey] = expanded;
        return TrySave(flags);
    }

    private Dictionary<string, bool> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new Dictionary<string, bool>(StringComparer.Ordinal);
            }

            var bytes = PreferenceFile.ReadOrNull(_filePath);
            if (bytes is null)
            {
                return new Dictionary<string, bool>(StringComparer.Ordinal);
            }

            return JsonSerializer.Deserialize<Dictionary<string, bool>>(bytes, JsonOptions)
                ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            NotSupportedException or
            ArgumentException)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }
    }

    private bool TrySave(Dictionary<string, bool> flags)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(flags, JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }
}
