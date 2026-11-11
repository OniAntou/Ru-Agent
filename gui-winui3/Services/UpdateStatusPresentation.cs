namespace RuApp.Services;

/// <summary>Where the About page's update row stands; one state decides its headline and buttons.</summary>
public enum UpdateUiState
{
    /// <summary>An installed release that has not been checked yet.</summary>
    Idle,
    Checking,
    Available,
    UpToDate,
    Failed,
    Downloading,
    Installing,
    Cancelled,
    /// <summary>A source build: updated by rebuilding, never in place.</summary>
    SourceBuild,
    /// <summary>The installation record could not be read.</summary>
    Unavailable,
}

/// <summary>
/// The words and buttons for each <see cref="UpdateUiState"/>. The headline names the
/// state; the longer sentence (what is kept, what went wrong) sits under it as the
/// row's description.
/// </summary>
public static class UpdateStatusPresentation
{
    public static string Headline(UpdateUiState state, string? version = null) => state switch
    {
        UpdateUiState.Checking => "Checking for updates…",
        UpdateUiState.Available => string.IsNullOrWhiteSpace(version) ? "An update is available" : $"Ru {version} is available",
        UpdateUiState.UpToDate => "Ru is up to date",
        UpdateUiState.Failed => "Couldn't check for updates",
        UpdateUiState.Downloading => string.IsNullOrWhiteSpace(version) ? "Downloading the update" : $"Downloading Ru {version}",
        UpdateUiState.Installing => "Installing the update",
        UpdateUiState.Cancelled => "Update cancelled",
        UpdateUiState.SourceBuild => "Source build",
        UpdateUiState.Unavailable => "Updates unavailable",
        _ => "Not checked yet",
    };

    /// <summary>The label of the check button, which doubles as the retry.</summary>
    public static string CheckLabel(UpdateUiState state) => state switch
    {
        UpdateUiState.Available => "Check again",
        UpdateUiState.Failed => "Try again",
        _ => "Check for updates",
    };

    /// <summary>False while an update is moving or when there is nothing to check.</summary>
    public static bool ShowCheck(UpdateUiState state) =>
        state is not (UpdateUiState.Downloading or UpdateUiState.Installing
            or UpdateUiState.SourceBuild or UpdateUiState.Unavailable);

    /// <summary>The install button exists only while there is a release to install.</summary>
    public static bool ShowInstall(UpdateUiState state) =>
        state is UpdateUiState.Available or UpdateUiState.Cancelled;

    /// <summary>Only a download can be cancelled; once installing starts there is no way back.</summary>
    public static bool ShowCancel(UpdateUiState state) => state == UpdateUiState.Downloading;

    /// <summary>Progress belongs to a download in flight.</summary>
    public static bool ShowProgress(UpdateUiState state) =>
        state is UpdateUiState.Downloading or UpdateUiState.Installing;
}
