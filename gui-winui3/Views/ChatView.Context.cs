using System;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class ChatView
    {
        private bool _suppressContextUsageUpdate;

        public void UpdateContextUsage(bool force = false)
        {
            if (_suppressContextUsageUpdate) return;
            // This walks the whole transcript, so running it on every streamed
            // snapshot turns chat history length into per-frame UI cost and
            // starves the activity feed. A one-second-old meter is plenty fresh.
            // Session open refills the transcript inside this window after
            // clearing it, so it repaints by force once history lands.
            long now = Environment.TickCount64;
            if (!force && now - _lastContextUsageTick < ContextUsageMinIntervalMs)
            {
                return;
            }

            _lastContextUsageTick = now;
            var retainedRun = _agentController?.CurrentRun;
            if (retainedRun is null || retainedRun.SessionId != _activeSessionId)
                retainedRun = _activeAgentUsage?.ContextSnapshotRun is { } saved && saved.SessionId == _activeSessionId ? saved : null;
            bool isRetainedRun = retainedRun is not null
                && string.Equals(retainedRun.SessionId, _activeSessionId, StringComparison.Ordinal);
            _retainedContextMeter.ScopeToRun(isRetainedRun ? retainedRun : null, _activeSessionId);
            if (retainedRun is not null && ReferenceEquals(retainedRun, _activeAgentUsage?.ContextSnapshotRun))
                _retainedContextMeter.RestoreUsage(retainedRun.RunId, retainedRun.SessionId, retainedRun.ProviderProtocol, _activeAgentUsage!);
            int? retainedContextWindow = RetainedRunPresentation.PublishedContextWindow(retainedRun, _activeSessionId);
            int maxTokens;
            string displayLimit;
            if (isRetainedRun)
            {
                maxTokens = retainedContextWindow ?? 0;
                displayLimit = ModelContextSpecs.FormatPublishedContext(retainedContextWindow) ?? string.Empty;
                RetainedRunIdentityText.Text = RetainedRunPresentation.Identity(retainedRun!);
                RetainedRunPinWell.Visibility = Visibility.Visible;
            }
            else
            {
                int? catalogCtx = ModelSelection.ContextLength();
                maxTokens = catalogCtx is > 0 ? catalogCtx.Value : 0;
                displayLimit = ModelContextSpecs.FormatPublishedContext(catalogCtx) ?? string.Empty;
                RetainedRunIdentityText.Text = string.Empty;
                RetainedRunPinWell.Visibility = Visibility.Collapsed;
            }

            // Include draft input so the meter moves while typing, not only after send.
            string draftText = string.Empty;
            try
            {
                draftText = ChatInputTextBox?.Text ?? string.Empty;
            }
            catch
            {
                // Input box may not be ready during init; estimate stays history-only.
            }

            var measured = _retainedContextMeter.Measure(
                Messages.Select(message => (
                    (string?)message.Content,
                    message.Attachments?.Count(attachment => attachment.IsImage) ?? 0,
                    message.Attachments?.Where(attachment => !attachment.IsImage)
                        .Sum(attachment => attachment.TextContent?.Length ?? 0) ?? 0)).ToList(),
                draftText,
                Composer.ImageCount,
                Composer.Attachments.Where(attachment => !attachment.IsImage)
                    .Sum(attachment => attachment.TextContent?.Length ?? 0));

            int estimatedTokens = measured.UsedTokens;
            if (!measured.HasKnownHistoryBoundary) { maxTokens = 0; displayLimit = string.Empty; }
            var meter = AgentContextMeterPolicy.Evaluate(maxTokens, displayLimit, estimatedTokens, 0);
            int usedTokens = meter.UsedTokens;
            int draftEstimate = ModelContextSpecs.EstimateTokens(draftText.Length
                + Composer.Attachments.Where(item => !item.IsImage).Sum(item => item.TextContent?.Length ?? 0), Composer.ImageCount);
            var reading = ContextPanelPresenter.Project(_retainedContextMeter.ContextTokens, draftEstimate,
                measured.EstimatedTokens, measured.HasKnownHistoryBoundary);
            PaintContextBreakdown(reading, usedTokens, meter.MaximumTokens);
            AutomationProperties.SetHelpText(ContextMeterButton,
                (isRetainedRun ? RetainedRunIdentityText.Text + ". " : string.Empty)
                + $"Context used ~{ModelContextSpecs.FormatTokens(usedTokens)} tokens. "
                + $"System prompt {reading.System}; Tool definitions {reading.Tools}; Messages {reading.Messages}. "
                + reading.Note);

            if (meter.HasBudget)
            {
                double percentage = meter.Percentage;

                ContextProgressRing.Value = percentage;

                var brush = meter.Severity switch
                {
                    AgentContextMeterSeverity.Danger =>
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuDangerBrush"],
                    AgentContextMeterSeverity.Warning =>
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuWarningBrush"],
                    _ => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuAccentBrush"],
                };

                ContextProgressRing.Foreground = brush;

                ContextUsedLabel.Text = $"~{ModelContextSpecs.FormatTokens(usedTokens)} / {meter.DisplayLimit}";
                ContextPercentLabel.Text = percentage.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "% of context used";
                ContextPercentLabel.Foreground = meter.Severity == AgentContextMeterSeverity.Normal
                    ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"]
                    : brush;
            }
            else
            {
                ContextProgressRing.Value = 0;
                ContextUsedLabel.Text = $"~{ModelContextSpecs.FormatTokens(usedTokens)} tokens";
                ContextPercentLabel.Text = "Context size unknown";
                ContextPercentLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"];
            }
        }

        private void PaintContextBreakdown(ContextPanelReading reading, int usedTokens, int maximumTokens)
        {
            ContextSystemLabel.Text = reading.System;
            ContextToolsLabel.Text = reading.Tools;
            ContextMessagesLabel.Text = reading.Messages;
            ContextEstimateNote.Text = reading.Note;
            ContextSegments.Visibility = reading.HasBreakdown ? Visibility.Visible : Visibility.Collapsed;
            ContextBreakdownFill.Background = (Brush)Application.Current.Resources["RuMutedBrush"];
            ContextBreakdownFill.Visibility = maximumTokens > 0 && usedTokens > 0 ? Visibility.Visible : Visibility.Collapsed;
            ContextBreakdownBar.ColumnDefinitions[0].Width = new GridLength(Math.Clamp((double)usedTokens, 0, Math.Max(0, maximumTokens)), GridUnitType.Star);
            ContextBreakdownBar.ColumnDefinitions[1].Width = new GridLength(Math.Max(0L, (long)maximumTokens - usedTokens), GridUnitType.Star);
            ContextSegments.ColumnDefinitions[0].Width = new GridLength(reading.SystemWeight, GridUnitType.Star);
            ContextSegments.ColumnDefinitions[1].Width = new GridLength(reading.ToolWeight, GridUnitType.Star);
            ContextSegments.ColumnDefinitions[2].Width = new GridLength(reading.MessageWeight, GridUnitType.Star);
        }

        private void OnContextEstimateReceived(object? sender, AgentContextEstimateEventArgs args)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnContextEstimateReceived(sender, args));
                return;
            }
            if (_agentController?.CurrentRun is not { } run || run.RunId != args.RunId || run.SessionId != _activeSessionId) return;
            _retainedContextMeter.ScopeToRun(run, _activeSessionId);
            if (_retainedContextMeter.ObserveEstimate(args.RunId, args.SessionId, args.Estimate)) UpdateContextUsage(force: true);
        }

        private void OnChatInputTyping(object sender, TextChangedEventArgs e)
        {
            Composer.EditDraft(_activeSessionId, ChatInputTextBox.Text);
            ScheduleContextMeterUpdate();
        }

        private DispatcherQueueTimer? _contextMeterTimer;
        private bool _contextMeterScheduled;
        private long _lastContextUsageTick;
        private const long ContextUsageMinIntervalMs = 1000;

        private void ScheduleContextMeterUpdate()
        {
            if (_suppressContextUsageUpdate)
            {
                return;
            }

            if (_contextMeterTimer is null)
            {
                _contextMeterTimer = DispatcherQueue.CreateTimer();
                _contextMeterTimer.Interval = Services.UiTiming.ContextMeterDebounce;
                _contextMeterTimer.Tick += OnContextMeterTimerTick;
            }

            if (_contextMeterScheduled)
            {
                return;
            }

            _contextMeterScheduled = true;
            _contextMeterTimer.Start();
        }

        private void OnContextMeterTimerTick(object? sender, object e)
        {
            _contextMeterTimer?.Stop();
            _contextMeterScheduled = false;
            UpdateContextUsage();
        }
    }
}
