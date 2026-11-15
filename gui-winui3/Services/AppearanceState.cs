namespace RuApp.Services;

/// <summary>
/// The one live copy of the appearance preferences. Settings edits it, the shell
/// listens to <see cref="Changed"/> and repaints, and every change is saved at once.
/// Fonts and corner radii are read into XAML resources before the first window is
/// built, so those two take effect on the next launch; <see cref="StartupPreferences"/>
/// records what this launch actually applied so Settings can say a restart is pending.
/// </summary>
public sealed class AppearanceState
{
    public static AppearanceState Instance { get; } = new(new AppearancePreferenceStore(), new AppearanceMediaFiles());

    private readonly AppearancePreferenceStore _store;

    public AppearanceState(AppearancePreferenceStore store, AppearanceMediaFiles media)
    {
        _store = store;
        Media = media;
        Current = store.Load();
        StartupPreferences = Current;
    }

    public AppearanceMediaFiles Media { get; }

    public AppearancePreferences Current { get; private set; }

    public AppearancePreferences StartupPreferences { get; }

    public event EventHandler? Changed;

    /// <summary>True when the fonts or corner style differ from what this launch applied.</summary>
    public bool RestartPending =>
        !string.Equals(Current.UiFont, StartupPreferences.UiFont, StringComparison.Ordinal)
        || !string.Equals(Current.CodeFont, StartupPreferences.CodeFont, StringComparison.Ordinal)
        || Current.CornerStyle != StartupPreferences.CornerStyle;

    /// <summary>The background file's full path, or null when there is none or it went missing.</summary>
    public string? BackgroundPath => Current.HasBackground ? Media.Resolve(Current.BackgroundFile) : null;

    /// <summary>Sanitizes, saves and publishes a change. Returns false when the file could not be written.</summary>
    public bool Update(Func<AppearancePreferences, AppearancePreferences> change)
    {
        var next = change(Current).Sanitized();
        if (next == Current) return true;
        var previousFile = Current.BackgroundFile;
        Current = next;
        bool saved = _store.TrySave(next);
        // Listeners swap the backdrop first, which releases a playing video's file;
        // only then can the old media be deleted.
        Changed?.Invoke(this, EventArgs.Empty);
        if (saved && !string.Equals(previousFile, next.BackgroundFile, StringComparison.Ordinal))
        {
            Media.DeleteAllExcept(next.BackgroundFile);
        }

        return saved;
    }

    /// <summary>Back to the stock look: no background, theme colors, stock fonts and layout.</summary>
    public bool Reset() => Update(_ => AppearancePreferences.Default);
}
