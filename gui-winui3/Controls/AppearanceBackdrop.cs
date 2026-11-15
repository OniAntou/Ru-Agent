using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RuApp.Services;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace RuApp.Controls;

/// <summary>
/// The user's background picture or video, under the whole window, with a scrim of
/// the theme canvas on top so text stays readable. A video plays muted and looped,
/// pauses while the window is hidden, and holds still when Windows animations are
/// off. A file that fails to decode clears the backdrop and raises
/// <see cref="MediaFailed"/> instead of leaving a broken frame.
/// </summary>
public sealed class AppearanceBackdrop : Grid
{
    private readonly Image _image = new() { IsHitTestVisible = false };
    private readonly Border _scrim = new() { IsHitTestVisible = false };
    private MediaPlayerElement? _videoElement;
    private MediaPlayer? _player;
    private string? _shownPath;
    private AppearanceBackgroundKind _shownKind;
    private bool _windowVisible = true;

    public AppearanceBackdrop()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        _image.ImageFailed += (_, _) => Fail();
        _scrim.Background = (Brush)Application.Current.Resources["RuCanvasBrush"];
        Children.Add(_image);
        Children.Add(_scrim);
    }

    /// <summary>Raised on the UI thread when the chosen file cannot be shown.</summary>
    public event EventHandler? MediaFailed;

    public bool IsShowing => Visibility == Visibility.Visible;

    public void Apply(AppearancePreferences preferences, string? path)
    {
        var kind = path is null ? AppearanceBackgroundKind.None : preferences.BackgroundKind;
        _scrim.Opacity = preferences.PanelOpacity;
        var stretch = preferences.BackgroundFit switch
        {
            AppearanceBackgroundFit.Fit => Stretch.Uniform,
            AppearanceBackgroundFit.Stretch => Stretch.Fill,
            AppearanceBackgroundFit.Center => Stretch.None,
            _ => Stretch.UniformToFill,
        };
        _image.Stretch = stretch;
        if (_videoElement is not null) _videoElement.Stretch = stretch;

        if (kind == _shownKind && string.Equals(path, _shownPath, StringComparison.OrdinalIgnoreCase)) return;
        Clear();
        _shownKind = kind;
        _shownPath = path;
        switch (kind)
        {
            case AppearanceBackgroundKind.Image:
                _image.Source = new BitmapImage(new Uri(path!));
                _image.Visibility = Visibility.Visible;
                Visibility = Visibility.Visible;
                break;
            case AppearanceBackgroundKind.Video:
                ShowVideo(path!, stretch);
                Visibility = Visibility.Visible;
                break;
            default:
                Visibility = Visibility.Collapsed;
                break;
        }
    }

    /// <summary>Pauses a video while the window is minimized or hidden and resumes it after.</summary>
    public void SetWindowVisible(bool visible)
    {
        _windowVisible = visible;
        UpdatePlayback();
    }

    private void ShowVideo(string path, Stretch stretch)
    {
        _player = new MediaPlayer
        {
            IsLoopingEnabled = true,
            IsMuted = true,
            AutoPlay = false,
            Source = MediaSource.CreateFromUri(new Uri(path)),
        };
        _player.MediaFailed += OnPlayerFailed;
        _videoElement = new MediaPlayerElement
        {
            Stretch = stretch,
            IsHitTestVisible = false,
            AreTransportControlsEnabled = false,
        };
        _videoElement.SetMediaPlayer(_player);
        _image.Visibility = Visibility.Collapsed;
        Children.Insert(0, _videoElement);
        UpdatePlayback();
    }

    private void UpdatePlayback()
    {
        if (_player is null) return;
        if (_windowVisible && UiTiming.AnimationsEnabled) _player.Play();
        else _player.Pause();
    }

    private void OnPlayerFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            if (ReferenceEquals(sender, _player)) Fail();
        });

    private void Fail()
    {
        Clear();
        _shownKind = AppearanceBackgroundKind.None;
        _shownPath = null;
        Visibility = Visibility.Collapsed;
        MediaFailed?.Invoke(this, EventArgs.Empty);
    }

    private void Clear()
    {
        _image.Source = null;
        if (_player is not null)
        {
            _player.MediaFailed -= OnPlayerFailed;
            _player.Pause();
            _player.Dispose();
            _player = null;
        }

        if (_videoElement is not null)
        {
            _videoElement.SetMediaPlayer(null);
            Children.Remove(_videoElement);
            _videoElement = null;
        }
    }
}
