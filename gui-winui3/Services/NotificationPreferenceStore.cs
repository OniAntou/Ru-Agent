using System.Text.Json;

namespace RuApp.Services;

/// <summary>
/// What Ru tells the user while it is in the background. Taskbar flash and sound are
/// on by default because they need nothing from Windows; Windows notifications
/// (toasts) are opt-in because turning them on registers Ru with Windows for the
/// current user.
/// </summary>
public sealed record NotificationPreferences(
    bool OnFinished = true,
    bool OnNeedsAttention = true,
    bool FlashTaskbar = true,
    bool PlaySound = true,
    bool ShowToast = false)
{
    public static NotificationPreferences Default { get; } = new();

    public bool AnyEvent => OnFinished || OnNeedsAttention;
}

/// <summary>
/// The notification preferences: one bounded JSON file under the app's data root,
/// written atomically. A missing or unreadable file reads as the defaults.
/// </summary>
public sealed class NotificationPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public NotificationPreferenceStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "notifications.json");
    }

    public NotificationPreferences Load()
    {
        try
        {
            var bytes = File.Exists(_filePath) ? PreferenceFile.ReadOrNull(_filePath) : null;
            return bytes is null
                ? NotificationPreferences.Default
                : JsonSerializer.Deserialize<NotificationPreferences>(bytes, JsonOptions) ?? NotificationPreferences.Default;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
                or NotSupportedException or ArgumentException)
        {
            return NotificationPreferences.Default;
        }
    }

    public bool TrySave(NotificationPreferences preferences)
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
