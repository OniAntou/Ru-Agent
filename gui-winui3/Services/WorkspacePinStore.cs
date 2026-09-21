using System.Text.Json;

namespace RuApp.Services;

/// <summary>User-pinned workspace directories, shown even with zero sessions.</summary>
public sealed class WorkspacePinStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public WorkspacePinStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "workspace-pinned.json");
    }

    public static string Canonicalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public IReadOnlyList<string> PinnedPaths => Load();

    public bool Pin(string path)
    {
        var canonical = Canonicalize(path);
        var pins = Load();
        if (pins.Contains(canonical, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        pins.Add(canonical);
        return TrySave(pins);
    }

    public bool Unpin(string path)
    {
        var canonical = Canonicalize(path);
        var pins = Load();
        var removed = pins.RemoveAll(pin => string.Equals(pin, canonical, StringComparison.OrdinalIgnoreCase));
        return removed > 0 && TrySave(pins);
    }

    private List<string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            var bytes = PreferenceFile.ReadOrNull(_filePath);
            if (bytes is null)
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<string>>(bytes, JsonOptions) ?? [];
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            NotSupportedException or
            ArgumentException)
        {
            return [];
        }
    }

    private bool TrySave(List<string> pins)
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
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(pins, JsonOptions));
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
