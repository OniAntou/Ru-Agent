using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Services;

namespace RuApp.Controls;

/// <summary>
/// A slow push-in while loading: the meteor field and the wordmark scale up at different
/// rates, so the layers read as depth rather than one flat picture moving.
/// </summary>
public sealed partial class WelcomeOverlay
{
    private const double CameraSeconds = 16;
    private readonly ScaleTransform _fieldCamera = new();
    private readonly ScaleTransform _contentCamera = new();
    private Storyboard? _cameraPush;

    private void AttachCamera(UIElement content)
    {
        Attach(_meteors, _fieldCamera);
        Attach(content, _contentCamera);

        static void Attach(UIElement? element, ScaleTransform transform)
        {
            if (element is null) return;
            element.RenderTransform = transform;
            element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        }
    }

    private void StartCameraPush()
    {
        if (_disposed || _isReady || _isDismissed || !UiTiming.AnimationsEnabled) return;
        _cameraPush?.Stop();
        var push = new Storyboard();
        Add(_fieldCamera, 1.05);
        Add(_contentCamera, 1.09);
        _cameraPush = push;
        push.Begin();

        void Add(ScaleTransform transform, double to)
        {
            foreach (var property in new[] { "ScaleX", "ScaleY" })
            {
                var animation = new DoubleAnimation
                {
                    To = to, Duration = TimeSpan.FromSeconds(CameraSeconds),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(animation, transform);
                Storyboard.SetTargetProperty(animation, property);
                push.Children.Add(animation);
            }
        }
    }

    // Stopping a storyboard snaps its targets back; keep the frame the reader last saw
    // unless the screen is being settled to its resting state.
    private void StopCamera(bool settle)
    {
        double field = _fieldCamera.ScaleX, content = _contentCamera.ScaleX;
        _cameraPush?.Stop();
        _cameraPush = null;
        Set(_fieldCamera, settle ? 1 : field);
        Set(_contentCamera, settle ? 1 : content);

        static void Set(ScaleTransform transform, double value)
        {
            transform.ScaleX = value;
            transform.ScaleY = value;
        }
    }
}