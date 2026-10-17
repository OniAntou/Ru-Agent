using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Services;

namespace RuApp.Controls;

public sealed partial class AgentSidePanelControl
{
    private Storyboard? _visibilityAnimation;
    public bool IsVisibilityExiting { get; private set; }

    public void CancelVisibilityAnimations()
    {
        var animation = _visibilityAnimation;
        _visibilityAnimation = null;
        IsVisibilityExiting = false;
        animation?.Stop();
        CardHost.Opacity = PanelHost.Opacity = 1;
        CardSlide.X = PanelSlide.X = 0;
    }

    private void CancelVisibilityExit()
    {
        if (IsVisibilityExiting) CancelVisibilityAnimations();
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
        CancelVisibilityAnimations();
        if (!UiTiming.AnimationsEnabled)
        {
            completed?.Invoke();
            return;
        }

        IsVisibilityExiting = exiting;
        host.Opacity = exiting ? 1 : 0;
        slide.X = exiting ? 0 : 12;
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
        animation.Completed += (_, _) =>
        {
            // A cancelled transition cannot complete a newer visibility request.
            if (!ReferenceEquals(_visibilityAnimation, animation)) return;
            _visibilityAnimation = null;
            IsVisibilityExiting = false;
            animation.Stop();
            host.Opacity = 1;
            slide.X = 0;
            completed?.Invoke();
        };
        animation.Begin();
    }

    private static void PlayBodyTransition(FrameworkElement body)
    {
        if (!UiTiming.AnimationsEnabled) return;
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(140) };
        Storyboard.SetTarget(fade, body);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var animation = new Storyboard();
        animation.Children.Add(fade);
        animation.Begin();
    }
}
