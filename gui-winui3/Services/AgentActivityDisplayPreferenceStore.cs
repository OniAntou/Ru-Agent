using System.Text.Json;

namespace RuApp.Services;

/// <summary>Device-local transcript presentation, independent of backend settings.</summary>
public sealed class AgentActivityDisplayPreferenceStore
{
    private readonly string _filePath;

    public AgentActivityDisplayPreferenceStore(string? filePath = null) =>
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ru", "activity-display.json");

    public bool LoadCompact()
    {
        try
        {
            var bytes = PreferenceFile.ReadOrNull(_filePath);
            // A new install starts compact, matching the grouped process view; an
            // existing file keeps whatever the user last chose.
            if (bytes is null) return true;
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            return root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("compact", out var compact)
                || compact.ValueKind != JsonValueKind.False;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return true;
        }
    }

    public bool TrySaveCompact(bool compact)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new { compact }));
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
