using System.Text.Json;

namespace RuApp.Services;

public enum UiTheme
{
    Light,
    Dark
}

public sealed class UiThemePreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public UiThemePreferenceStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "ui-preferences.json");
    }

    public UiTheme Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return UiTheme.Light;
            }

            var bytes = PreferenceFile.ReadOrNull(_filePath);
            if (bytes is null)
            {
                return UiTheme.Light;
            }

            var preferences = JsonSerializer.Deserialize<UiThemePreferences>(bytes, JsonOptions);

            if (!Enum.TryParse(preferences?.Theme, ignoreCase: true, out UiTheme theme)
                || !Enum.IsDefined(typeof(UiTheme), theme))
            {
                return UiTheme.Light;
            }

            return theme;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            NotSupportedException or
            ArgumentException)
        {
            return UiTheme.Light;
        }
    }

    public bool TrySave(UiTheme theme)
    {
        if (!Enum.IsDefined(typeof(UiTheme), theme))
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
            var json = JsonSerializer.Serialize(new UiThemePreferences(theme.ToString()), JsonOptions);
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

    private sealed record UiThemePreferences(string? Theme);
}
