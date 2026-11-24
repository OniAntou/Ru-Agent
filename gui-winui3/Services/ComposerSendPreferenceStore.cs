using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuApp.Services;

/// <summary>What Enter does while a run is working (ADR 0056).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ComposerWhileRunning>))]
public enum ComposerWhileRunning
{
    /// <summary>The message goes into the working run, read at its next step. The default.</summary>
    Steer,

    /// <summary>The message waits and is sent as the next turn when the run ends.</summary>
    Queue,
}

public sealed record ComposerSendPreferences(ComposerWhileRunning WhileRunning = ComposerWhileRunning.Steer)
{
    public static ComposerSendPreferences Default { get; } = new();
}

/// <summary>
/// The composer's send preferences: one bounded JSON file under the app's data root,
/// written atomically. A missing or unreadable file reads as the defaults (steer). The
/// shared instance caches what it last read or saved, so the composer can ask on every Enter.
/// </summary>
public sealed class ComposerSendPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;
    private ComposerSendPreferences? _current;

    public static ComposerSendPreferenceStore Shared { get; } = new();

    public ComposerSendPreferenceStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "composer.json");
    }

    public ComposerSendPreferences Current => _current ??= Load();

    public ComposerSendPreferences Load()
    {
        try
        {
            var bytes = File.Exists(_filePath) ? PreferenceFile.ReadOrNull(_filePath) : null;
            var loaded = bytes is null
                ? ComposerSendPreferences.Default
                : JsonSerializer.Deserialize<ComposerSendPreferences>(bytes, JsonOptions) ?? ComposerSendPreferences.Default;
            return Enum.IsDefined(loaded.WhileRunning) ? loaded : ComposerSendPreferences.Default;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
                or NotSupportedException or ArgumentException)
        {
            return ComposerSendPreferences.Default;
        }
    }

    public bool TrySave(ComposerSendPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
            _current = preferences;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
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
