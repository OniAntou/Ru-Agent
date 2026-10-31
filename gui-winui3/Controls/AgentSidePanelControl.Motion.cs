using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Services;

namespace RuApp.Controls;

public sealed partial class AgentSidePanelControl
{
    private Storyboard? _visibilityAnimation;
    private FrameworkElement? _visibilityHost;
    private Storyboard? _bodyAnimation;
    private FrameworkElement? _bodyHost;
    public bool IsVisibilityExiting { get; private set; }

    public void CancelVisibilityAnimations()
    {
        var animation = _visibilityAnimation;
        _visibilityAnimation = null;
        _visibilityHost = null;
        IsVisibilityExiting = false;
        animation?.Stop();
        CardHost.Opacity = PanelHost.Opacity = 1;
        CardSlide.X = PanelSlide.X = 0;
    }

    private void CancelVisibilityExit()
    {
        if (IsVisibilityExiting) PlayVisibilityEntrance();
    }

    public void PlayVisibilityEntrance() =>
        PlayEntrance(_mode == AgentSidePanelMode.Panel ? PanelHost : CardHost,
            _mode == AgentSidePanelMode.Panel ? PanelSlide : CardSlide);

    public void PlayVisibilityExit(Action completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (IsVisibilityExiting) return;
        PlaySurfaceTransition(_mode == AgentSidePanelMode.Panel ? PanelHost : CardHost,
            _mode == AgentSidePanelMode.Panel ? PanelSlide : CardSlide, exiting: true, completed);
    }

    private void PlayEntrance(FrameworkElement host, TranslateTransform slide) =>
        PlaySurfaceTransition(host, slide, exiting: false, null);

    private void PlaySurfaceTransition(FrameworkElement host, TranslateTransform slide, bool exiting, Action? completed)
    {
        // Stop() removes animated values. Snapshot the presented state first so
        // reversing a transition continues from the frame the user actually saw.
        bool continuing = _visibilityAnimation is not null && ReferenceEquals(_visibilityHost, host);
        double fromOpacity = continuing ? host.Opacity : exiting ? 1 : 0;
        double fromX = continuing ? slide.X : exiting ? 0 : 12;
        CancelVisibilityAnimations();
        if (!UiTiming.AnimationsEnabled)
        {
            completed?.Invoke();
            return;
        }

        IsVisibilityExiting = exiting;
        host.Opacity = fromOpacity;
        slide.X = fromX;
        var animation = new Storyboard();
        var duration = TimeSpan.FromMilliseconds(exiting ? 140 : 180);
        var fade = new DoubleAnimation { To = exiting ? 0 : 1, Duration = duration };
        Storyboard.SetTarget(fade, host);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var glide = new DoubleAnimation
        {
            To = exiting ? 12 : 0,
            Duration = duration,
            EasingFunction = new ExponentialEase { EasingMode = exiting ? EasingMode.EaseIn : EasingMode.EaseOut },
        };
        Storyboard.SetTarget(glide, slide);
        Storyboard.SetTargetProperty(glide, "X");
        animation.Children.Add(fade);
        animation.Children.Add(glide);
        _visibilityAnimation = animation;
        _visibilityHost = host;
        animation.Completed += (_, _) =>
        {
            // A cancelled transition cannot complete a newer visibility request.
            if (!ReferenceEquals(_visibilityAnimation, animation)) return;
            _visibilityAnimation = null;
            _visibilityHost = null;
            IsVisibilityExiting = false;
            animation.Stop();
            host.Opacity = 1;
            slide.X = 0;
            completed?.Invoke();
        };
        animation.Begin();
    }

    private void CancelBodyTransition()
    {
        _bodyAnimation?.Stop();
        _bodyAnimation = null;
        if (_bodyHost is { } host) host.Opacity = 1;
        _bodyHost = null;
    }

    private void PlayBodyTransition(FrameworkElement body)
    {
        CancelBodyTransition();
        if (!UiTiming.AnimationsEnabled) return;
        // Keep the incoming content readable immediately; stale clocks must never
        // leave a previously hidden terminal or preview translucent on return.
        var fade = new DoubleAnimation { From = 0.72, To = 1, Duration = TimeSpan.FromMilliseconds(120) };
        Storyboard.SetTarget(fade, body);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var animation = new Storyboard();
        animation.Children.Add(fade);
        _bodyAnimation = animation;
        _bodyHost = body;
        animation.Completed += (_, _) =>
        {
            if (ReferenceEquals(_bodyAnimation, animation)) CancelBodyTransition();
        };
        animation.Begin();
    }
}
