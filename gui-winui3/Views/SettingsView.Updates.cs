using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuAgent.Client;
using RuAgent.Updates;

namespace RuApp.Views;

public sealed partial class SettingsView
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly UpdatePreferences _updatePreferences = new();
    private ReleaseUpdate? _availableUpdate;
    private InstalledRelease? _installedRelease;
    private bool _updatesInitializing;
    private CancellationTokenSource? _updateDownload;
    public Func<bool>? CanInstallUpdate { get; set; }
    public event Action? UpdateRestartRequested;

    public void ShowUpdates()
    {
        SelectTab(isGeneral: true);
        PanelGeneralScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        CheckUpdatesButton.Focus(FocusState.Programmatic);
        if (CheckUpdatesButton.IsEnabled)
            OnCheckUpdatesClick(this, new RoutedEventArgs());
    }

    private void InitializeUpdates()
    {
        _updatesInitializing = true;
        try
        {
            UpdateChannelCombo.SelectedIndex = _updatePreferences.LoadChannel() == UpdateChannel.Preview ? 1 : 0;
            _installedRelease = InstalledRelease.Load(AppContext.BaseDirectory);
            UpdateVersionText.Text = "Ru " + (_installedRelease?.Version ?? InstalledRelease.SoftwareVersion + " · source build");
            UpdateStatusText.Text = _installedRelease is null
                ? "Install a release package to enable in-app updates. Source builds are updated by rebuilding."
                : "Updates install only when you choose. Sessions and API keys are kept.";
            if (_updatePreferences.LoadLastResult() is { } last) UpdateStatusText.Text = last;
            CheckUpdatesButton.IsEnabled = _installedRelease is not null;
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
        { UpdateStatusText.Text = "The installation information could not be read. Reinstall a release package."; CheckUpdatesButton.IsEnabled = false; }
        finally { _updatesInitializing = false; }
    }

    private void OnUpdateChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatesInitializing || UpdateStatusText is null) return;
        try
        {
            _updatePreferences.SaveChannel(UpdateChannelCombo.SelectedIndex == 1 ? UpdateChannel.Preview : UpdateChannel.Stable);
            _availableUpdate = null;
            InstallUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "Channel changed. Check for updates to see available releases.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { UpdateStatusText.Text = "Could not save the update channel."; }
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_installedRelease is null) return;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateChannelCombo.IsEnabled = false;
        UpdateStatusText.Text = "Checking releases…";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var client = new ReleaseUpdateClient();
            _availableUpdate = await client.CheckAsync(_installedRelease, _updatePreferences.LoadChannel(), deadline.Token);
            UpdateStatusText.Text = _availableUpdate is null ? "No newer compatible release is available on this channel." : $"Ru {_availableUpdate.Version} is available.";
            InstallUpdateButton.IsEnabled = _availableUpdate is not null;
        }
        catch (OperationCanceledException) { if (!_updateLifetime.IsCancellationRequested) UpdateStatusText.Text = "Update check timed out. Try again."; }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { UpdateStatusText.Text = $"Could not check for updates: {ex.Message}"; }
        finally
        {
            if (!_updateLifetime.IsCancellationRequested) { CheckUpdatesButton.IsEnabled = true; UpdateChannelCombo.IsEnabled = true; }
        }
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        var release = _availableUpdate;
        if (release is null || _installedRelease is null) return;
        if (CanInstallUpdate?.Invoke() != true) { UpdateStatusText.Text = "Finish or stop the current Agent run before updating."; return; }
        InstallUpdateButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        UpdateChannelCombo.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        CancelUpdateButton.Visibility = Visibility.Visible;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(10));
        _updateDownload = operation;
        UpdateStatusText.Text = "Downloading and verifying update…";
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
            CancelUpdateButton.Visibility = Visibility.Collapsed;
            await UpdateMaintenance.PrepareAsync(operation.Token);
            await UpdateLauncher.StartAsync(downloaded, AppContext.BaseDirectory, _updatePreferences.DirectoryPath, restartDesktop: true, operation.Token);
            UpdateStatusText.Text = "Installing verified update. Ru will restart.";
            UpdateRestartRequested?.Invoke();
        }
        catch (OperationCanceledException) { if (!_updateLifetime.IsCancellationRequested) UpdateStatusText.Text = "Update cancelled."; }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { UpdateStatusText.Text = $"Could not install update: {ex.Message}. Finish active runs or close other Ru windows, then retry."; }
        finally
        {
            _updateDownload = null;
            if (!_updateLifetime.IsCancellationRequested)
            { CancelUpdateButton.Visibility = Visibility.Collapsed; UpdateProgress.Visibility = Visibility.Collapsed; CheckUpdatesButton.IsEnabled = true; InstallUpdateButton.IsEnabled = true; UpdateChannelCombo.IsEnabled = true; }
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
