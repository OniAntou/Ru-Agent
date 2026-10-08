using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The empty-state hero's motion and content: a staggered entrance the first
/// time it appears, a quick cross-fade with the transcript when the first
/// message sends or a session clears, and the live workspace line naming where
/// Ru is working. Transform and opacity only, reduced-motion aware.
/// </summary>
public sealed partial class ChatView
{
    private bool _welcomeHeroVisible = true;
    private bool _welcomeHeroEntrancePlayed;
    private Storyboard? _welcomeHeroFade;
    private Storyboard? _chatContentFade;

    private void UpdateWelcomeHero(bool hasMessages)
    {
        if (WelcomeHeroPanel is null || ChatContentGrid is null)
        {
            return;
        }

        UpdateWelcomeWorkspaceText();

        bool showHero = !hasMessages;
        if (showHero && !_welcomeHeroEntrancePlayed)
        {
            // First paint: the hero is already visible, so play the authored
            // entrance instead of treating this as a state change.
            _welcomeHeroVisible = true;
            if (UiTiming.AnimationsEnabled)
            {
                PlayWelcomeHeroEntrance();
            }

            return;
        }

        if (_welcomeHeroVisible == showHero)
        {
            return;
        }

        _welcomeHeroVisible = showHero;

        if (!UiTiming.AnimationsEnabled)
        {
            StopWelcomeStoryboards();
            WelcomeHeroPanel.Visibility = showHero ? Visibility.Visible : Visibility.Collapsed;
            WelcomeHeroPanel.Opacity = 1;
            if (WelcomeHeroSlide is not null)
            {
                WelcomeHeroSlide.Y = 0;
            }

            ChatContentGrid.Visibility = showHero ? Visibility.Collapsed : Visibility.Visible;
            ChatContentGrid.Opacity = 1;
            return;
        }

        if (showHero)
        {
            WelcomeHeroPanel.Visibility = Visibility.Visible;
            PlayWelcomeHeroEntrance();
            HideChatContentAnimated();
        }
        else
        {
            ShowChatContentAnimated();
            HideWelcomeHeroAnimated();
        }
    }

    /// <summary>Names the folder Ru is working in; refreshed with the session.</summary>
    private void UpdateWelcomeWorkspaceText()
    {
        if (WelcomeWorkspaceText is null)
        {
            return;
        }

        string workspace = string.IsNullOrWhiteSpace(_agentWorkspace) ? "." : _agentWorkspace;
        WelcomeWorkspaceText.Text = workspace;
        ToolTipService.SetToolTip(WelcomeWorkspaceText, workspace);
    }

    private void PlayWelcomeHeroEntrance()
    {
        _welcomeHeroFade?.Stop();
        _welcomeHeroFade = null;
        NormalizeWelcomeHeroElements();

        WelcomeHeroPanel.Opacity = 1;
        if (WelcomeHeroSlide is not null)
        {
            WelcomeHeroSlide.Y = 0;
        }

        if (_welcomeHeroEntrancePlayed)
        {
            _welcomeHeroFade = FadeTo(WelcomeHeroPanel, 0, 1, milliseconds: 160, delayMs: 0, onCompleted: null);
            return;
        }

        _welcomeHeroEntrancePlayed = true;

        var storyboard = new Storyboard();
        if (WelcomeHeroSlide is not null)
        {
            WelcomeHeroSlide.Y = 10;
            var rise = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(320),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(rise, WelcomeHeroSlide);
            Storyboard.SetTargetProperty(rise, "Y");
            storyboard.Children.Add(rise);
        }

        var stagger = new (UIElement Element, int DelayMs)[]
        {
            (WelcomeWorkspaceRow, 0),
            (WelcomeTitleText, 50),
        };
        foreach (var (element, delayMs) in stagger)
        {
            if (element is null)
            {
                continue;
            }

            element.Opacity = 0;
            var fade = new DoubleAnimation
            {
                To = 1,
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                Duration = TimeSpan.FromMilliseconds(240),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, element);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
        }

        storyboard.Completed += (_, _) => NormalizeWelcomeHeroElements();
        _welcomeHeroFade = storyboard;
        storyboard.Begin();
    }

    private void NormalizeWelcomeHeroElements()
    {
        WelcomeWorkspaceRow.Opacity = 1;
        WelcomeTitleText.Opacity = 1;
    }

    private void ShowChatContentAnimated()
    {
        _chatContentFade?.Stop();
        ChatContentGrid.Visibility = Visibility.Visible;
        _chatContentFade = FadeTo(ChatContentGrid, 0, 1, milliseconds: 200, delayMs: 0, onCompleted: null);
    }

    private void HideChatContentAnimated()
    {
        _chatContentFade?.Stop();
        var storyboard = FadeTo(ChatContentGrid, ChatContentGrid.Opacity, 0, milliseconds: 140, delayMs: 0, onCompleted: null);
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_chatContentFade, storyboard))
            {
                ChatContentGrid.Visibility = Visibility.Collapsed;
                ChatContentGrid.Opacity = 1;
            }
        };
        _chatContentFade = storyboard;
    }

    private void HideWelcomeHeroAnimated()
    {
        _welcomeHeroFade?.Stop();
        var storyboard = FadeTo(WelcomeHeroPanel, WelcomeHeroPanel.Opacity, 0, milliseconds: 140, delayMs: 0, onCompleted: null);
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_welcomeHeroFade, storyboard))
            {
                WelcomeHeroPanel.Visibility = Visibility.Collapsed;
                WelcomeHeroPanel.Opacity = 1;
                NormalizeWelcomeHeroElements();
            }
        };
        _welcomeHeroFade = storyboard;
    }

    private static Storyboard FadeTo(
        FrameworkElement element,
        double from,
        double to,
        int milliseconds,
        int delayMs,
        Action? onCompleted)
    {
        element.Opacity = from;
        var fade = new DoubleAnimation
        {
            To = to,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        if (onCompleted is not null)
        {
            storyboard.Completed += (_, _) => onCompleted();
        }

        storyboard.Begin();
        return storyboard;
    }

    private void StopWelcomeStoryboards()
    {
        _welcomeHeroFade?.Stop();
        _welcomeHeroFade = null;
        _chatContentFade?.Stop();
        _chatContentFade = null;
    }
}
