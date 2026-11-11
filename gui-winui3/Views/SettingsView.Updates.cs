using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuAgent.Client;
using RuAgent.Updates;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class SettingsView
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly UpdatePreferences _updatePreferences = new();
    private ReleaseUpdate? _availableUpdate;
    private InstalledRelease? _installedRelease;
    private bool _updatesInitializing;
    private UpdateUiState _updateState = UpdateUiState.Idle;
    private int _updateChannelIndex;
    private CancellationTokenSource? _updateDownload;
    private UpdateAvailabilityMonitor? _updateMonitor;
    private Task? _updateMonitorTask;
    public Func<bool>? CanInstallUpdate { get; set; }
    public event Action? UpdateRestartRequested;
    public event Action<bool>? UpdateAvailabilityChanged;

    public void StartUpdateAvailabilityChecks()
    {
        if (_installedRelease is null || _updateLifetime.IsCancellationRequested) return;
        if (_updateMonitorTask is null && _updateMonitor is not null)
            _updateMonitorTask = ObserveUpdateMonitorAsync(_updateMonitor.Start());
    }

    private void StopUpdateAvailabilityChecks()
    {
        _updateMonitor?.Stop();
    }

    private async void CheckForUpdates()
    {
        if (CheckUpdatesButton.IsEnabled && !_updateLifetime.IsCancellationRequested && _updateMonitor is not null)
            await _updateMonitor.CheckNowAsync(_updateLifetime.Token);
    }

    private void SetAvailableUpdate(ReleaseUpdate? release)
    {
        _availableUpdate = release;
        InstallUpdateButton.IsEnabled = release is not null;
        UpdateAvailabilityChanged?.Invoke(release is not null);
    }

    public void ShowUpdates()
    {
        SelectTab(SettingsTab.About);
        PanelAboutScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        (_availableUpdate is null ? CheckUpdatesButton : InstallUpdateButton).Focus(FocusState.Programmatic);
        if (_availableUpdate is null) CheckForUpdates();
    }

    private void InitializeUpdates()
    {
        _updateMonitor = new UpdateAvailabilityMonitor(CheckUpdateAvailabilityAsync, ReportUpdateCheckFailure);
        _updatesInitializing = true;
        AboutVersionText.Text = "Version " + InstalledRelease.SoftwareVersion;
        AboutBuildText.Text = "Source build";
        SetUpdateState(UpdateUiState.SourceBuild, animate: false);
        AboutPlatformText.Text = "Windows · " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        try
        {
            SetChannelSelection(_updatePreferences.LoadChannel() == UpdateChannel.Preview ? 1 : 0);
            _installedRelease = InstalledRelease.Load(AppContext.BaseDirectory);
            AboutVersionText.Text = "Version " + (_installedRelease?.Version ?? InstalledRelease.SoftwareVersion);
            AboutBuildText.Text = _installedRelease is null ? "Source build" : "Installed Desktop release";
            UpdateStatusText.Text = _installedRelease is null
                ? "Install a release package to enable in-app updates. Source builds are updated by rebuilding."
                : "Updates install only when you choose. Sessions and API keys are kept.";
            if (_updatePreferences.LoadLastResult() is { } last) UpdateStatusText.Text = last;
            CheckUpdatesButton.IsEnabled = _installedRelease is not null;
            SetUpdateState(_installedRelease is null ? UpdateUiState.SourceBuild : UpdateUiState.Idle, animate: false);
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
        {
            AboutBuildText.Text = "Installation details unavailable";
            UpdateStatusText.Text = "The installation information could not be read. Reinstall a release package.";
            CheckUpdatesButton.IsEnabled = false;
            SetUpdateState(UpdateUiState.Unavailable, animate: false);
        }
        finally { _updatesInitializing = false; }
    }

    private void OnUpdateChannelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        int index = tag == "preview" ? 1 : 0;
        bool changed = index != _updateChannelIndex;
        // A click on the channel already chosen must not leave it looking unchosen.
        SetChannelSelection(index);
        if (!changed || _updatesInitializing || UpdateStatusText is null) return;
        SetAvailableUpdate(null);
        try
        {
            _updatePreferences.SaveChannel(index == 1 ? UpdateChannel.Preview : UpdateChannel.Stable);
            SetUpdateState(UpdateUiState.Idle);
            UpdateStatusText.Text = "Channel changed. Check for updates to see available releases.";
            CheckForUpdates();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { UpdateStatusText.Text = "Could not save the update channel."; }
    }

    private void SetChannelSelection(int index)
    {
        _updateChannelIndex = index;
        UpdateStableOption.IsChecked = index == 0;
        UpdatePreviewOption.IsChecked = index == 1;
        PlaceSegmentThumb(UpdateChannelThumb);
    }

    /// <summary>The channel is locked while a check or a download is running.</summary>
    private void SetChannelEnabled(bool enabled)
    {
        UpdateStableOption.IsEnabled = enabled;
        UpdatePreviewOption.IsEnabled = enabled;
        UiMotion.FadeOpacity(UpdateChannelChoice, enabled ? 1 : 0.5);
    }

    /// <summary>
    /// Puts the update row in one state: the headline, the check label, which of the
    /// buttons and the progress line exist. The description under the headline is set
    /// by the caller, because only it knows what went wrong or what is kept.
    /// </summary>
    private void SetUpdateState(UpdateUiState state, string? version = null, bool animate = true)
    {
        _updateState = state;
        var headline = UpdateStatusPresentation.Headline(state, version);
        if (!string.Equals(UpdateHeadlineText.Text, headline, StringComparison.Ordinal))
        {
            UpdateHeadlineText.Text = headline;
            if (animate && IsLoaded)
            {
                // The headline follows the state: it fades in rather than swapping.
                UpdateHeadlineText.Opacity = 0;
                UiMotion.FadeOpacity(UpdateHeadlineText, 1);
            }
        }

        CheckUpdatesButton.Content = UpdateStatusPresentation.CheckLabel(state);
        ShowUpdateElement(CheckUpdatesButton, UpdateStatusPresentation.ShowCheck(state), animate);
        ShowUpdateElement(InstallUpdateButton, UpdateStatusPresentation.ShowInstall(state), animate);
        ShowUpdateElement(CancelUpdateButton, UpdateStatusPresentation.ShowCancel(state), animate);
        ShowUpdateElement(UpdateProgress, UpdateStatusPresentation.ShowProgress(state), animate);
        UpdateStatusText.Visibility = string.IsNullOrWhiteSpace(UpdateStatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowUpdateElement(FrameworkElement element, bool visible, bool animate)
    {
        if (animate && IsLoaded) UiMotion.SetVisible(element, visible);
        else element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_updateMonitor is null) return;
        await _updateMonitor.CheckNowAsync(_updateLifetime.Token);
    }

    private async Task CheckUpdateAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (_installedRelease is null || !CheckUpdatesButton.IsEnabled || _updateLifetime.IsCancellationRequested) return;
        SetAvailableUpdate(null);
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        SetChannelEnabled(false);
        UpdateStatusText.Text = string.Empty;
        SetUpdateState(UpdateUiState.Checking);
        try
        {
            using var client = new ReleaseUpdateClient();
            var channel = UpdateAvailabilityMonitor.ChannelFromSelection(_updateChannelIndex);
            var release = await client.CheckAsync(_installedRelease, channel, cancellationToken);
            if (_updateLifetime.IsCancellationRequested) return;
            SetAvailableUpdate(release);
            UpdateStatusText.Text = _availableUpdate is null
                ? "No newer compatible release is available on this channel."
                : "Sessions and API keys are kept. It installs only when you choose.";
            SetUpdateState(_availableUpdate is null ? UpdateUiState.UpToDate : UpdateUiState.Available, _availableUpdate?.Version);
            InstallUpdateButton.IsEnabled = _availableUpdate is not null;
        }
        catch (OperationCanceledException)
        {
            if (!_updateLifetime.IsCancellationRequested)
            {
                UpdateStatusText.Text = "The request timed out.";
                SetUpdateState(UpdateUiState.Failed);
            }
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            UpdateStatusText.Text = ex.Message;
            SetUpdateState(UpdateUiState.Failed);
        }
        finally
        {
            if (!_updateLifetime.IsCancellationRequested) { CheckUpdatesButton.IsEnabled = true; SetChannelEnabled(true); }
        }
    }

    private async Task ObserveUpdateMonitorAsync(Task monitorTask)
    {
        try { await monitorTask; }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportUpdateCheckFailure(error); }
    }

    private void ReportUpdateCheckFailure(Exception error)
    {
        if (_updateLifetime.IsCancellationRequested) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_updateLifetime.IsCancellationRequested) return;
            UpdateStatusText.Text = error is TimeoutException ? "The request timed out." : error.Message;
            SetUpdateState(UpdateUiState.Failed);
        });
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        var release = _availableUpdate;
        if (release is null || _installedRelease is null) return;
        if (CanInstallUpdate?.Invoke() != true) { UpdateStatusText.Text = "Finish or stop the current Agent run before updating."; return; }
        InstallUpdateButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        SetChannelEnabled(false);
        UpdateProgress.Value = 0;
        UpdateStatusText.Text = "Downloading and verifying. Ru restarts when it finishes.";
        SetUpdateState(UpdateUiState.Downloading, release.Version);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(10));
        _updateDownload = operation;
        try
        {
            using var client = new ReleaseUpdateClient();
            var staging = Path.Combine(_updatePreferences.DirectoryPath, Guid.NewGuid().ToString("N"));
            var progress = new UpdateProgressReporter(value => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_updateLifetime.IsCancellationRequested) UpdateProgress.Value = value * 100;
            }));
            var downloaded = await client.DownloadAsync(release, staging, progress, operation.Token);
            if (CanInstallUpdate?.Invoke() != true) throw new UpdateException("update_busy");
            UpdateStatusText.Text = "Ru will restart.";
            SetUpdateState(UpdateUiState.Installing, release.Version);
            await UpdateMaintenance.PrepareAsync(operation.Token);
            await UpdateLauncher.StartAsync(downloaded, AppContext.BaseDirectory, _updatePreferences.DirectoryPath, restartDesktop: true, operation.Token);
            UpdateRestartRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            if (!_updateLifetime.IsCancellationRequested)
            {
                UpdateStatusText.Text = "Nothing was installed. Install again whenever you like.";
                SetUpdateState(UpdateUiState.Cancelled, release.Version);
            }
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // The release is still there to install: the row returns to Available with the reason under it.
            UpdateStatusText.Text = $"Couldn't install: {ex.Message}. Finish active runs or close other Ru windows, then retry.";
            SetUpdateState(UpdateUiState.Available, release.Version);
        }
        finally
        {
            _updateDownload = null;
            if (!_updateLifetime.IsCancellationRequested)
            { CheckUpdatesButton.IsEnabled = true; InstallUpdateButton.IsEnabled = true; SetChannelEnabled(true); }
        }
    }

    private void OnCancelUpdateClick(object sender, RoutedEventArgs e) => _updateDownload?.Cancel();

    private sealed class UpdateProgressReporter(Action<double> report) : IProgress<double>
    {
        private int _last = -1;
        public void Report(double value)
        {
            var percent = (int)(value * 100);
            if (percent == _last) return;
            _last = percent;
            report(value);
        }
    }
}
