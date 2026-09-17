using System.Text.Json;

namespace RuApp.Services;

/// <summary>Persists the conversation transcript font size (14-17, default 14) as local JSON.</summary>
public sealed class TranscriptFontSizeStore
{
    public const double MinimumSize = 14;
    public const double MaximumSize = 17;
    public const double DefaultSize = 14;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public TranscriptFontSizeStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "transcript.json");
    }

    public double GetSize()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return DefaultSize;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            if (document.RootElement.TryGetProperty("message_font_size", out var size)
                && size.ValueKind == JsonValueKind.Number)
            {
                return Clamp(size.GetDouble());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
        }

        return DefaultSize;
    }

    public bool SetSize(double size)
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
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new { message_font_size = Clamp(size) }, JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
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

    public static double Clamp(double size) =>
        Math.Min(MaximumSize, Math.Max(MinimumSize, size));
}
