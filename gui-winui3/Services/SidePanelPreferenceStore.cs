using System.Text.Json;

namespace RuApp.Services;

/// <summary>
/// The persisted width of the right side panel: one bounded JSON file under the
/// app's data root, written atomically. A missing, oversized, or unreadable
/// file reads as the default width, and a stored value is clamped before it is
/// handed out, so a hand-edited file cannot size the panel past its bounds.
/// </summary>
public sealed class SidePanelPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public SidePanelPreferenceStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "side-panel.json");
    }

    public double Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return AgentSidePanelPolicy.DefaultWidth;
            }

            var bytes = PreferenceFile.ReadOrNull(_filePath);
            if (bytes is null)
            {
                return AgentSidePanelPolicy.DefaultWidth;
            }

            var preferences = JsonSerializer.Deserialize<SidePanelPreferences>(bytes, JsonOptions);
            return AgentSidePanelPolicy.ClampWidth(preferences?.Width ?? AgentSidePanelPolicy.DefaultWidth);
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            NotSupportedException or
            ArgumentException)
        {
            return AgentSidePanelPolicy.DefaultWidth;
        }
    }

    public bool TrySave(double width)
    {
        if (!double.IsFinite(width))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(
                new SidePanelPreferences(AgentSidePanelPolicy.ClampWidth(width)),
                JsonOptions);
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

    private sealed record SidePanelPreferences(double? Width);
}
