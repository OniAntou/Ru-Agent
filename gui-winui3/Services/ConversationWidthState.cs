using System.ComponentModel;
using System.Text.Json;

namespace RuApp.Services;

/// <summary>
/// The user-draggable width of the conversation column, ported from DSH's
/// ConversationWidthControls. Two edge handles change the same bounded value;
/// the width is centered and persisted across restarts.
/// </summary>
public sealed class ConversationWidthState : INotifyPropertyChanged
{
    public const double MinimumWidth = 640;
    public const double EdgeBudget = 176;
    public const double DefaultWidth = 720;
    public const double MaximumWidth = 920;
    /// <summary>Distance from the content edge to the start of the grab strip.</summary>
    public const double HandleOutset = 34;

    private readonly ConversationWidthStore _store;
    private double? _preference;
    private double? _savedPreference;
    private double _viewportWidth = 1200;
    private double _contentWidth = DefaultWidth;

    public ConversationWidthState() : this(null) { }

    public ConversationWidthState(ConversationWidthStore? store)
    {
        _store = store ?? new ConversationWidthStore();
        _savedPreference = _store.Load();
        _preference = _savedPreference;
        Recompute();
    }

    public double ContentWidth
    {
        get => _contentWidth;
        private set
        {
            var clamped = Math.Max(MinimumWidth, Math.Round(value));
            if (Math.Abs(clamped - _contentWidth) < 0.5) return;
            _contentWidth = clamped;
            OnPropertyChanged(nameof(ContentWidth));
            OnPropertyChanged(nameof(HandleLayerWidth));
        }
    }

    /// <summary>The transparent handle layer, with 34 px of room outside each content edge.</summary>
    public double HandleLayerWidth => ContentWidth + HandleOutset * 2;

    public double ViewportWidth => _viewportWidth;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetViewportWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0) return;
        _viewportWidth = width;
        Recompute();
    }

    /// <summary>Live pointer preview; does not write the preference file.</summary>
    public void PreviewWidth(double width)
    {
        if (!double.IsFinite(width)) return;
        _preference = Clamp(width);
        Recompute();
    }

    /// <summary>Persists the current drag result; a press-only gesture keeps the old value.</summary>
    public void Commit()
    {
        if (_preference is not { } preference) return;
        _savedPreference = preference;
        _store.TrySave(preference);
    }

    public void CancelDrag()
    {
        _preference = _savedPreference;
        Recompute();
    }

    private void Recompute()
    {
        var maximum = Math.Max(MinimumWidth, _viewportWidth - EdgeBudget);
        var width = _preference is { } preference
            ? Clamp(preference)
            : Math.Max(680, Math.Min(_viewportWidth * 0.64, MaximumWidth));
        ContentWidth = Math.Min(width, maximum);
    }

    private double Clamp(double width)
    {
        var maximum = Math.Max(MinimumWidth, _viewportWidth - EdgeBudget);
        return Math.Min(Math.Max(width, MinimumWidth), maximum);
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Bounded local preference file for the conversation width, in pixels.</summary>
public sealed class ConversationWidthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public ConversationWidthStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "conversation-width.json");
    }

    public double? Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var bytes = PreferenceFile.ReadOrNull(_filePath);
            if (bytes is null) return null;
            var preferences = JsonSerializer.Deserialize<ConversationWidthPreference>(bytes, JsonOptions);
            return preferences?.Width is { } width && double.IsFinite(width) && width > 0 ? width : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public bool TrySave(double width)
    {
        if (!double.IsFinite(width) || width <= 0) return false;
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
                new ConversationWidthPreference(width), JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
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

    private sealed record ConversationWidthPreference(double? Width);
}
