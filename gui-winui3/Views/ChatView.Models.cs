using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Controls;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Windows.Storage.Streams;
using System.IO;

namespace RuApp.Views
{
    public sealed partial class ChatView
    {
        /// <summary>
        /// Width the reasoning rows reserve for the margin beside the selection rule, so
        /// every label starts on the same line whether or not the rule is drawn.
        /// </summary>
        /// <remarks>
        /// The same 11px the model picker reserves, and for the same reason: a rule that
        /// only exists on the selected row must not push that row's label out of line
        /// with the other three.
        /// </remarks>
        private const double ReasoningSelectionColumnWidth = 11;

        private readonly List<Button> _reasoningRows = new();
        private readonly List<string> _reasoningEfforts = new();
        private OptionListKeyboard _reasoningKeyboard = new(0, _ => { }, () => { });
        private Brush? _reasoningCursorBrush;
        private Brush? _reasoningRestBrush;

        /// <summary>The composer's provider/model and reasoning-effort state.</summary>
        public ChatModelPickerViewModel ModelSelection { get; }

        public Task LoadChatModelsAsync() => ModelSelection.LoadAsync();

        /// <summary>
        /// The catalog for the selected backend, or null when none has arrived; the
        /// session usage panel prices runs from it.
        /// </summary>
        public ProviderCatalogState? ProviderCatalogState => ModelSelection.SelectedCatalog;

        private void AttachModelSelection()
        {
            if (_apiService is null || _providerCatalogController is null || _groupedCatalogs is null) return;
            ModelSelection.Attach(
                new ApiChatModelBackend(_apiService),
                new ProviderCatalogSession(_providerCatalogController),
                new GroupedCatalogSession(_groupedCatalogs));
        }

        private void OnModelSelectionPickerReady(ProviderModelPickerProjection projection) => ModelPicker.Apply(projection);

        private void OnModelSelectionChanged() => UpdateContextUsage();

        private void OnChatModelFlyoutOpened(object sender, object e)
        {
            ModelPicker.FocusSearch();
            ModelSelection.OnPickerOpened();
        }

        private void OnChatModelFlyoutClosed(object sender, object e) => ModelSelection.OnPickerClosed();

        private void OnModelPickerSearchTextChanged(object? sender, string searchText) =>
            ModelSelection.SetSearchText(searchText);

        private void OnModelPickerModelSelected(object? sender, ProviderModelSelectionEventArgs e)
        {
            ChatModelFlyout.Hide();
            _ = ModelSelection.SelectModelAsync(e.Model, e.Backend);
        }

        /// <summary>
        /// Escape, or any other way the list asked to be left without a choice. The
        /// flyout belongs to this view (it sits on the toolbar button), so it hides here;
        /// light dismiss alone is unreliable while a text field holds the focus.
        /// </summary>
        private void OnModelPickerDismissRequested(object? sender, EventArgs e) =>
            ChatModelFlyout.Hide();

        /// <summary>
        /// Builds the reasoning effort list.
        /// </summary>
        /// <remarks>
        /// The rows are the same shape the model picker uses, and deliberately so: one
        /// style from App.xaml, the same 28px rhythm, the same rule in the margin for the
        /// row in use, the same cursor fill for the row the keys are on. Two option lists
        /// in one toolbar answering the same question should not be two designs, and a
        /// shared style makes the next change to one a change to both rather than
        /// something somebody has to remember.
        ///
        /// The check glyph is gone for the reason it is gone from the model picker: a rule
        /// in the margin is found faster in a list that moves, and the check was a second
        /// signal for a fact the rule already carries. It was also not doing what its
        /// comment claimed. The old row held the check in an auto-width column and
        /// collapsed it when the row was not selected, so the label did shift sideways by
        /// the width of a check on every state change; "always present" was true of the
        /// object and false of the layout.
        ///
        /// No descriptions. "low" and "high" are the levels the provider publishes, and
        /// any sentence written under them about what they feel like would be prose nobody
        /// can check. The values are the interface.
        ///
        /// The container takes the focus and the rows are not tab stops, so Tab leaves the
        /// flyout rather than walking four buttons to get out of it.
        /// </remarks>
        private void OnChatReasoningFlyoutOpened(object sender, object e)
        {
            ReasoningOptionsContainer.Children.Clear();
            _reasoningRows.Clear();
            _reasoningEfforts.Clear();
            var options = ModelSelection.ReasoningOptions();

            var resources = Application.Current.Resources;
            var font = (FontFamily)resources["RuUiFontFamily"];
            var ink = (Brush)resources["RuInkBrush"];
            var accent = (Brush)resources["RuAccentBrush"];
            _reasoningCursorBrush = (Brush)resources["RuAccentSoftBrush"];
            _reasoningRestBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var rowStyle = (Style)resources["RuOptionRowStyle"];

            int selectedIndex = 0;
            foreach (var option in options)
            {
                bool isSelected = string.Equals(option.Value, ModelSelection.SelectedEffort, StringComparison.OrdinalIgnoreCase);
                string label = option.Label ?? option.Value;
                int index = _reasoningRows.Count;
                if (isSelected)
                {
                    selectedIndex = index;
                }

                var btn = new Button
                {
                    Style = rowStyle,
                    Background = _reasoningRestBrush,
                    Tag = index,
                };
                AutomationProperties.SetName(btn, $"Reasoning effort: {label}");
                btn.PointerEntered += OnReasoningRowPointerEntered;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ReasoningSelectionColumnWidth) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                if (isSelected)
                {
                    var bar = new Border
                    {
                        Width = 3,
                        Background = accent,
                        Margin = new Thickness(2, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Stretch,
                    };
                    Grid.SetColumn(bar, 0);
                    grid.Children.Add(bar);
                }

                var titleBlock = new TextBlock
                {
                    Text = label,
                    FontFamily = font,
                    FontSize = 13,
                    Foreground = ink,
                    FontWeight = isSelected
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(titleBlock, 1);
                grid.Children.Add(titleBlock);

                btn.Content = grid;
                btn.Click += OnReasoningOptionClicked;
                ReasoningOptionsContainer.Children.Add(btn);
                _reasoningRows.Add(btn);
                _reasoningEfforts.Add(option.Value);
            }

            _reasoningKeyboard = new OptionListKeyboard(
                _reasoningRows.Count,
                ActivateReasoningRow,
                () => ChatReasoningFlyout.Hide());
            _reasoningKeyboard.CursorChanged += OnReasoningCursorChanged;
            _reasoningKeyboard.MoveCursor(selectedIndex);

            ReasoningOptionsContainer.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// The pointer is the cursor, exactly as in the model picker: one marked row, so
        /// "what will Enter pick" has a single answer whichever device the user reached
        /// for.
        /// </summary>
        private void OnReasoningRowPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button { Tag: int index })
            {
                _reasoningKeyboard.MoveCursor(index, show: false);
            }
        }

        private void OnReasoningCursorChanged(int index)
        {
            for (int i = 0; i < _reasoningRows.Count; i++)
            {
                _reasoningRows[i].Background = i == index ? _reasoningCursorBrush : _reasoningRestBrush;
            }
        }

        private void OnReasoningContainerKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // No text field holds the focus here, so Space is free to commit.
            if (_reasoningKeyboard.HandleKey(e.Key, spaceActivates: true))
            {
                e.Handled = true;
            }
        }

        private void ActivateReasoningRow(int index) => _ = CommitReasoningEffortAsync(index);

        private async void OnReasoningOptionClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: int index })
            {
                await CommitReasoningEffortAsync(index);
            }
        }

        private async Task CommitReasoningEffortAsync(int index)
        {
            try
            {
                if (index < 0 || index >= _reasoningEfforts.Count)
                {
                    return;
                }

                ChatReasoningFlyout.Hide();
                await ModelSelection.ApplyEffortAsync(_reasoningEfforts[index]);
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Reasoning effort selection failed: {ex.Message}");
            }
        }

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
            bool isRetainedRun = retainedRun is not null
                && string.Equals(retainedRun.SessionId, _activeSessionId, StringComparison.Ordinal);
            _retainedContextMeter.ScopeToRun(isRetainedRun ? retainedRun : null, _activeSessionId);
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

            int reportedTokens = 0;
            int estimatedTokens = measured.UsedTokens;
            if (!measured.HasKnownHistoryBoundary)
            {
                maxTokens = 0;
                displayLimit = string.Empty;
                ContextMessagesLegendLabel.Text = "Retained input unknown · draft estimate";
            }
            else
            {
                ContextMessagesLegendLabel.Text = measured.ReportedPromptTokens > 0
                    ? "Reported input + draft estimate"
                    : "Visible text/image estimate";
            }
            int? overheadTokens = AgentContextMeterPolicy.OverheadTokens(estimatedTokens, reportedTokens);
            var meter = AgentContextMeterPolicy.Evaluate(maxTokens, displayLimit, estimatedTokens, reportedTokens);
            int usedTokens = meter.UsedTokens;

            if (meter.HasBudget)
            {
                double percentage = meter.Percentage;

                ContextProgressRing.Value = percentage;
                PaintContextBreakdown(estimatedTokens, overheadTokens, usedTokens, meter.MaximumTokens);

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
                ContextPercentLabel.Text = percentage.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
                ContextPercentLabel.Foreground = meter.Severity == AgentContextMeterSeverity.Normal
                    ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"]
                    : brush;
            }
            else
            {
                ContextProgressRing.Value = 0;
                PaintContextBreakdown(estimatedTokens, overheadTokens, usedTokens, meter.MaximumTokens);
                ContextUsedLabel.Text = $"~{ModelContextSpecs.FormatTokens(usedTokens)} tokens";
                ContextPercentLabel.Text = "Used";
                ContextPercentLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"];
            }
        }

        /// <summary>
        /// Paints one flat share bar and its legend: messages from the local
        /// estimate, system plus tools only when a provider report reveals the
        /// gap. The fill spans only the used share of the budget track, with a
        /// hard gradient stop between the parts, so no seam ever shows. No
        /// report means no overhead row instead of a guess.
        /// </summary>
        private void PaintContextBreakdown(int messageTokens, int? overheadTokens, int usedTokens, int maximumTokens)
        {
            if (ContextBreakdownBar is null
                || ContextBreakdownFill is null
                || ContextStopMsg1 is null
                || ContextStopOv0 is null
                || ContextMessagesLabel is null
                || ContextOverheadRow is null
                || ContextOverheadLabel is null)
            {
                return;
            }

            ContextMessagesLabel.Text = $"~{ModelContextSpecs.FormatTokens(messageTokens)}";

            if (usedTokens <= 0 || maximumTokens <= 0)
            {
                ContextBreakdownFill.Visibility = Visibility.Collapsed;
                ContextOverheadRow.Visibility = Visibility.Collapsed;
                return;
            }

            ContextBreakdownFill.Visibility = Visibility.Visible;
            ContextBreakdownBar.ColumnDefinitions[0].Width =
                new GridLength(Math.Max(usedTokens, 0), GridUnitType.Star);
            ContextBreakdownBar.ColumnDefinitions[1].Width = new GridLength(
                Math.Max(maximumTokens - usedTokens, 0),
                GridUnitType.Star);
            int overhead = overheadTokens ?? 0;
            double ratio = overhead > 0
                ? Math.Max(0.0, Math.Min(1.0, (double)Math.Max(messageTokens, 0) / usedTokens))
                : 1.0;
            ContextStopMsg1.Offset = ratio;
            ContextStopOv0.Offset = ratio;

            if (overhead > 0)
            {
                ContextOverheadLabel.Text = $"~{ModelContextSpecs.FormatTokens(overhead)}";
                ContextOverheadRow.Visibility = Visibility.Visible;
            }
            else
            {
                ContextOverheadRow.Visibility = Visibility.Collapsed;
            }
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
