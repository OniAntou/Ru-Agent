using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuApp.Services;

/// <summary>
/// Persists <see cref="AppearancePreferences"/> as <c>%LOCALAPPDATA%\Ru\appearance.json</c>.
/// A separate file from <c>ui-preferences.json</c>: that store rewrites its file with
/// the theme alone, which would drop any field added beside it.
/// </summary>
public sealed class AppearancePreferenceStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new LenientEnumConverterFactory() },
    };

    /// <summary>
    /// Writes enums as kebab-case names. An unknown or non-string value reads as an
    /// undefined member, which <see cref="AppearancePreferences.Sanitized"/> turns into
    /// that field's default; one bad field must not throw away the rest of the file.
    /// </summary>
    private sealed class LenientEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private static readonly T Undefined = (T)Enum.ToObject(typeof(T), int.MinValue);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip();
                return Undefined;
            }

            var name = reader.GetString()?.Replace("-", string.Empty, StringComparison.Ordinal);
            return Enum.TryParse<T>(name, ignoreCase: true, out var value) && Enum.IsDefined(value) && !int.TryParse(name, out _)
                ? value
                : Undefined;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString()));
    }

    private readonly string _filePath;

    public AppearancePreferenceStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "appearance.json");
    }

    public AppearancePreferences Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return AppearancePreferences.Default;
            var bytes = PreferenceFile.ReadOrNull(_filePath);
            return bytes is null ? AppearancePreferences.Default : Parse(bytes) ?? AppearancePreferences.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AppearancePreferences.Default;
        }
    }

    public bool TrySave(AppearancePreferences preferences)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, Serialize(preferences.Sanitized()));
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string Serialize(AppearancePreferences preferences) =>
        JsonSerializer.Serialize(preferences, JsonOptions);

    /// <summary>Parses and sanitizes a preference document, or returns null when it is not one.</summary>
    public static AppearancePreferences? Parse(ReadOnlySpan<byte> json)
    {
        // Hand-edited or exported-then-edited files often carry a UTF-8 byte order mark.
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) json = json[3..];
        try
        {
            return JsonSerializer.Deserialize<AppearancePreferences>(json, JsonOptions)?.Sanitized();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A shareable theme: colors, fonts and layout. The background is left out, since
    /// it names a file in this machine's library that another machine does not have.
    /// </summary>
    public static string Export(AppearancePreferences preferences) =>
        Serialize(preferences.Sanitized() with
        {
            BackgroundKind = AppearanceBackgroundKind.None,
            BackgroundFile = null,
        });

    /// <summary>
    /// Applies an exported theme on top of <paramref name="current"/>. The file is
    /// untrusted input: it is size-capped, sanitized field by field, and never changes
    /// the background, which only a picked file can set.
    /// </summary>
    public static AppearancePreferences? Import(ReadOnlySpan<byte> json, AppearancePreferences current)
    {
        if (json.Length > PreferenceFile.MaximumBytes) return null;
        if (Parse(json) is not { } imported) return null;
        return imported with
        {
            BackgroundKind = current.BackgroundKind,
            BackgroundFile = current.BackgroundFile,
            BackgroundFit = current.BackgroundFit,
            PanelOpacity = current.PanelOpacity,
        };
    }
}
