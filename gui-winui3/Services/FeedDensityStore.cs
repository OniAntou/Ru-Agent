using System.Text.Json;

namespace RuApp.Services;

/// <summary>
/// Whether the agent feed renders compact: the settled detail lines hide.
/// Own file so theme and transcript-size writes never clobber it.
/// </summary>
public sealed class FeedDensityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public FeedDensityStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "feed-density.json");
    }

    public bool GetCompact()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            if (document.RootElement.TryGetProperty("compact", out var compact)
                && (compact.ValueKind == JsonValueKind.True || compact.ValueKind == JsonValueKind.False))
            {
                return compact.GetBoolean();
            }

            return false;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException or
            ArgumentException)
        {
            return false;
        }
    }

    public void SetCompact(bool compact)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new { compact }, JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException)
        {
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
