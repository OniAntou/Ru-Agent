using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Services;
using RuApp.ViewModels;

namespace RuApp.Views
{
    /// <summary>
    /// The conversation header above the transcript: a workspace › title
    /// breadcrumb, a read-only label for the permission preset, and a working
    /// indicator while a run is active. It shows with the transcript and stays
    /// away on the welcome screen.
    /// </summary>
    public sealed partial class ChatView
    {
        private const double ChatHeaderMinTitleWidth = 80;

        private long _chatHeaderVisibilityToken;
        private Storyboard? _chatHeaderRunPulse;

        private void InitializeChatHeader()
        {
            Presets.PropertyChanged += OnChatHeaderPresetChanged;
            RunPanel.PropertyChanged += OnChatHeaderRunChanged;
            ChatHeader.SizeChanged += OnChatHeaderSizeChanged;
            ChatHeaderPermission.SizeChanged += OnChatHeaderSizeChanged;
            ChatHeaderWorkspace.SizeChanged += OnChatHeaderSizeChanged;
            ChatHeaderRunStatus.SizeChanged += OnChatHeaderSizeChanged;
            _chatHeaderVisibilityToken = ChatContentGrid.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, OnChatHeaderHostVisibilityChanged);
            UpdateChatHeaderTitle();
            UpdateChatHeaderWorkspace();
            UpdateChatHeaderPermission();
            UpdateChatHeaderRunStatus();
            UpdateChatHeaderVisibility();
        }

        private void OnChatHeaderPresetChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SessionPresetViewModel.Preset)) UpdateChatHeaderPermission();
        }

        private void OnChatHeaderRunChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AgentRunPanelViewModel.IsRunning)) UpdateChatHeaderRunStatus();
        }

        private void OnChatHeaderHostVisibilityChanged(DependencyObject sender, DependencyProperty property)
            => UpdateChatHeaderVisibility();

        private void OnChatHeaderSizeChanged(object sender, SizeChangedEventArgs e) => FitChatHeaderTitle();

        private void UpdateChatHeaderVisibility()
        {
            ChatHeader.Visibility = ChatContentGrid.Visibility;
            UpdateChatHeaderRunStatus();
        }

        private void UpdateChatHeaderTitle()
        {
            var title = ChatHeaderPresentation.Title(_activeSessionTitle);
            ChatHeaderTitle.Text = title;
            ToolTipService.SetToolTip(ChatHeaderTitle, title);
        }

        private void UpdateChatHeaderWorkspace()
        {
            var name = ChatHeaderPresentation.WorkspaceName(_agentWorkspace);
            var visibility = name.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            ChatHeaderWorkspaceText.Text = name;
            ChatHeaderWorkspace.Visibility = visibility;
            ChatHeaderBreadcrumbSeparator.Visibility = visibility;
            ToolTipService.SetToolTip(ChatHeaderWorkspace, _agentWorkspace);
            AutomationProperties.SetName(ChatHeaderWorkspace, "Workspace: " + name);
        }

        private void UpdateChatHeaderPermission()
        {
            var permission = ChatHeaderPresentation.Permission(Presets.Preset);
            ChatHeaderPermissionText.Text = permission.Label;
            ChatHeaderPermissionDot.Fill = (Brush)Application.Current.Resources[permission.DotBrushKey];
            ToolTipService.SetToolTip(ChatHeaderPermission, permission.ToolTip);
            AutomationProperties.SetName(ChatHeaderPermission, "Permission: " + permission.Label);
        }

        // The dot breathes only while the header is on screen and a run is live.
        private void UpdateChatHeaderRunStatus()
        {
            var running = RunPanel.IsRunning;
            ChatHeaderRunStatus.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (running && ChatHeader.Visibility == Visibility.Visible && UiTiming.AnimationsEnabled)
            {
                if (_chatHeaderRunPulse is null)
                {
                    var pulse = new DoubleAnimation
                    {
                        From = 1,
                        To = 0.3,
                        Duration = TimeSpan.FromMilliseconds(900),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    };
                    Storyboard.SetTarget(pulse, ChatHeaderRunDot);
                    Storyboard.SetTargetProperty(pulse, "Opacity");
                    _chatHeaderRunPulse = new Storyboard();
                    _chatHeaderRunPulse.Children.Add(pulse);
                    _chatHeaderRunPulse.Begin();
                }
            }
            else
            {
                StopChatHeaderRunPulse();
            }
        }

        private void StopChatHeaderRunPulse()
        {
            _chatHeaderRunPulse?.Stop();
            _chatHeaderRunPulse = null;
            ChatHeaderRunDot.Opacity = 1;
        }

        // The label sits right after the title, so the title trims against the
        // width the other parts leave rather than pushing them off the bar.
        private void FitChatHeaderTitle()
        {
            var used = ChatHeaderPermission.ActualWidth + ChatHeaderLeading.Spacing;
            if (ChatHeaderWorkspace.Visibility == Visibility.Visible)
                used += ChatHeaderWorkspace.ActualWidth + ChatHeaderBreadcrumbSeparator.ActualWidth + 2 * ChatHeaderLeading.Spacing;
            if (ChatHeaderRunStatus.Visibility == Visibility.Visible)
                used += ChatHeaderRunStatus.ActualWidth + ChatHeaderLeading.Spacing;
            var available = ChatHeader.ActualWidth - ChatHeader.Padding.Left - ChatHeader.Padding.Right - used;
            ChatHeaderTitle.MaxWidth = Math.Max(ChatHeaderMinTitleWidth, available);
        }

        private void DisposeChatHeader()
        {
            StopChatHeaderRunPulse();
            Presets.PropertyChanged -= OnChatHeaderPresetChanged;
            RunPanel.PropertyChanged -= OnChatHeaderRunChanged;
            ChatHeader.SizeChanged -= OnChatHeaderSizeChanged;
            ChatHeaderPermission.SizeChanged -= OnChatHeaderSizeChanged;
            ChatHeaderWorkspace.SizeChanged -= OnChatHeaderSizeChanged;
            ChatHeaderRunStatus.SizeChanged -= OnChatHeaderSizeChanged;
            ChatContentGrid.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _chatHeaderVisibilityToken);
        }
    }
}
