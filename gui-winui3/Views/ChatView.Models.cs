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
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol.Harness;
using RuApp.Controls;
using RuApp.Models;
using RuApp.Services;
using Windows.Storage.Streams;
using System.IO;

namespace RuApp.Views
{
    public sealed partial class ChatView
    {
        public async Task LoadChatModelsAsync()
        {
            if (_apiService == null) return;
            try
            {
                var settings = await _apiService.GetSettingsAsync();
                if (settings == null) return;
                // Kept so a failed send can name the provider that has no key
                // without asking the backend again while reporting a failure.
                _currentSettings = settings;
                string backend = settings.BackendChoice ?? string.Empty;
                string currentModel = ProviderModelSelection.GetSelectedModel(settings, backend);

                _selectedModel = currentModel;
                _selectedBackend = backend;
                _groupedCatalogs?.SetSelection(backend, currentModel);
                await RefreshModelsDevGroupsAsync();
                ChatModelLabel.Text = ProviderModelPickerProjection.TriggerDisplay(backend, currentModel);
                if (_providerCatalogController != null)
                {
                    _providerCatalogController.SetBackend(backend);
                    await _providerCatalogController.RefreshAsync();
                }
                if (_groupedCatalogs != null)
                {
                    await _groupedCatalogs.RefreshAsync(force: false);
                }
                UpdateReasoningEffortUi();
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error loading models: {ex.Message}");
            }
        }

        private void OnChatModelFlyoutOpened(object sender, object e)
        {
            ModelPicker.FocusSearch();
            _ = RefreshGroupedCatalogsAsync(force: true);
        }

        private async Task RefreshGroupedCatalogsAsync(bool force)
        {
            if (_groupedCatalogs == null) return;
            await RefreshModelsDevGroupsAsync();
            await _groupedCatalogs.RefreshAsync(force);
        }

        /// <summary>
        /// Pulls the Models.dev provider list so the picker grows a group per
        /// generic provider. Best-effort: an empty list keeps the fixed five.
        /// </summary>
        private async Task RefreshModelsDevGroupsAsync()
        {
            if (_apiService == null || _groupedCatalogs == null) return;
            try
            {
                var providers = await _apiService.GetModelsDevAsync();
                await _groupedCatalogs.SetModelsDevProvidersAsync(
                    providers.Select(entry => new ModelsDevEntry(entry.Id, entry.Name)).ToArray());
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error loading Models.dev providers: {ex.Message}");
            }
        }

        private DispatcherQueueTimer? _modelSearchDebounce;
        private string _pendingModelSearch = string.Empty;

        private void OnModelPickerSearchTextChanged(object? sender, string searchText)
        {
            // Restarting the timer coalesces a burst of typing into one rebuild. The
            // previous shape called SetSearchText per keystroke, and every one of those
            // re-projected the catalog and rebuilt the whole dropdown on the UI thread.
            if (_modelSearchDebounce is null)
            {
                _modelSearchDebounce = DispatcherQueue.CreateTimer();
                _modelSearchDebounce.Interval = UiTiming.ModelSearchDebounce;
                _modelSearchDebounce.Tick += OnModelSearchDebounceTick;
            }

            _pendingModelSearch = searchText;
            _modelSearchDebounce.Stop();
            _modelSearchDebounce.Start();
        }

        private void OnModelSearchDebounceTick(object? sender, object e)
        {
            _modelSearchDebounce?.Stop();
            _groupedCatalogs?.SetSearchText(_pendingModelSearch);
        }

        private void OnModelPickerModelSelected(object? sender, ProviderModelSelectionEventArgs e)
        {
            ChatModelFlyout.Hide();
            _ = SelectChatModelAsync(e.Model, e.Backend);
        }

        private void OnGroupedStateChanged(object? sender, GroupedProviderCatalogState state)
        {
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (_groupedCatalogs is null ||
                    !ReferenceEquals(_groupedCatalogs.State, state))
                {
                    return;
                }

                ApplyGroupedState(state);
            }))
            {
                return;
            }
        }

        private void ApplyGroupedState(GroupedProviderCatalogState state)
        {
            if (!string.IsNullOrWhiteSpace(state.SelectedModel))
            {
                _selectedModel = state.SelectedModel;
            }
            if (!string.IsNullOrWhiteSpace(state.SelectedBackend))
            {
                _selectedBackend = state.SelectedBackend;
            }
            ChatModelLabel.Text = ProviderModelPickerProjection.TriggerDisplay(
                state.SelectedBackend, state.SelectedModel);
            ModelPicker.Apply(ProviderModelPickerProjection.Create(
                state.Catalogs.Values,
                state.SelectedBackend,
                state.SelectedModel,
                state.SearchText,
                _groupedCatalogs?.CurrentDefinitions));
            UpdateContextUsage();
            UpdateReasoningEffortUi();
        }

        private async Task SelectChatModelAsync(string model, string backend)
        {
            try
            {
                if (_apiService is null ||
                    string.IsNullOrWhiteSpace(backend) ||
                    string.IsNullOrWhiteSpace(model))
                {
                    ShowModelSaveFailure();
                    return;
                }

                if (await _apiService.SaveProviderModelSelectionAsync(backend, model))
                {
                    _selectedModel = model;
                    _selectedBackend = backend;
                    // Reset effort so stale value from previous model/backend cannot
                    // stick when the new model has different levels. Fresh server
                    // effort arrives via grouped refresh below.
                    _selectedReasoningEffort = "default";
                    _groupedCatalogs?.SetSelection(backend, model);
                    ChatModelLabel.Text = ProviderModelPickerProjection.TriggerDisplay(backend, model);
                    UpdateContextUsage();
                    if (_providerCatalogController != null)
                    {
                        _providerCatalogController.SetBackend(backend);
                        _ = _providerCatalogController.RefreshAsync();
                    }
                    // Refresh grouped catalogs in background to sync SelectedEffort;
                    // picker itself uses local _selectedModel instantly (no restart).
                    _ = _groupedCatalogs?.RefreshAsync(force: true);
                    UpdateReasoningEffortUi();
                }
                else
                {
                    ShowModelSaveFailure();
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error saving model: {ex.Message}");
                ShowModelSaveFailure();
            }
        }

        private void UpdateReasoningEffortUi()
        {
            SyncEffortFromCatalog();
            var options = GetCurrentReasoningOptions();

            if (!options.Any(o => string.Equals(o.Value, _selectedReasoningEffort, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedReasoningEffort = "default";
            }

            ChatReasoningLabel.Text = _selectedReasoningEffort;
            AutomationProperties.SetName(ChatReasoningButton, $"Reasoning effort: {_selectedReasoningEffort}");
        }

        private IReadOnlyList<ProviderReasoningOption> GetCurrentReasoningOptions()
        {
            if (_groupedCatalogs is null)
            {
                return [new ProviderReasoningOption("default", "default")];
            }

            var groupedState = _groupedCatalogs.State;
            string backend = !string.IsNullOrWhiteSpace(groupedState.SelectedBackend)
                ? groupedState.SelectedBackend
                : _selectedBackend;
            if (string.IsNullOrWhiteSpace(backend))
            {
                return [new ProviderReasoningOption("default", "default")];
            }

            if (groupedState.Catalogs.TryGetValue(backend, out var catalogState))
            {
                string effectiveModel = !string.IsNullOrWhiteSpace(groupedState.SelectedModel)
                    ? groupedState.SelectedModel
                    : _selectedModel;
                return ProviderReasoningOptions.For(catalogState, effectiveModel);
            }

            return [new ProviderReasoningOption("default", "default")];
        }

        private void SyncEffortFromCatalog(bool force = false)
        {
            if (_groupedCatalogs is null) return;

            var groupedState = _groupedCatalogs.State;
            if (string.IsNullOrWhiteSpace(groupedState.SelectedBackend)) return;

            if (groupedState.Catalogs.TryGetValue(groupedState.SelectedBackend, out var catalogState))
            {
                if (!string.IsNullOrWhiteSpace(catalogState.SelectedEffort) &&
                    (force ||
                    string.Equals(_selectedReasoningEffort, "default", StringComparison.OrdinalIgnoreCase)))
                {
                    _selectedReasoningEffort = catalogState.SelectedEffort;
                }
            }
        }

        private void OnChatReasoningFlyoutOpened(object sender, object e)
        {
            ReasoningOptionsContainer.Children.Clear();
            SyncEffortFromCatalog();
            var options = GetCurrentReasoningOptions();

            foreach (var option in options)
            {
                bool isSelected = string.Equals(option.Value, _selectedReasoningEffort, StringComparison.OrdinalIgnoreCase);

                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 6, 10, 6),
                    CornerRadius = new CornerRadius(5),
                    MinHeight = 30
                };
                btn.Resources["ButtonBackgroundPointerOver"] = (Brush)Application.Current.Resources["RuAccentSoftBrush"];
                btn.Resources["ButtonBackgroundPressed"] = (Brush)Application.Current.Resources["RuAccentSoftBrush"];
                btn.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                btn.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var titleBlock = new TextBlock
                {
                    Text = option.Label ?? option.Value,
                    FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["RuInkBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(titleBlock, 0);
                grid.Children.Add(titleBlock);

                if (isSelected)
                {
                    var checkIcon = new FontIcon
                    {
                        Glyph = "\uE73E",
                        FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["RuAccentBrush"],
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(16, 0, 2, 0)
                    };
                    Grid.SetColumn(checkIcon, 1);
                    grid.Children.Add(checkIcon);
                }

                btn.Content = grid;
                string effort = option.Value;
                btn.Click += async (s, args) =>
                {
                    try
                    {
                        ChatReasoningFlyout.Hide();
                        await ApplyReasoningEffortAsync(effort);
                    }
                    catch (Exception ex)
                    {
                        GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Reasoning effort selection failed: {ex.Message}");
                    }
                };

                ReasoningOptionsContainer.Children.Add(btn);
            }
        }

        private async Task ApplyReasoningEffortAsync(string effort)
        {
            string previousEffort = _selectedReasoningEffort;
            _selectedReasoningEffort = effort;
            ChatReasoningLabel.Text = effort;
            AutomationProperties.SetName(ChatReasoningButton, $"Reasoning effort: {effort}");

            if (_apiService is null) return;

            string backend = _groupedCatalogs?.State.SelectedBackend ?? string.Empty;
            string model = _selectedModel ?? string.Empty;
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(backend)) return;

            string identity = backend;
            if (_groupedCatalogs != null &&
                _groupedCatalogs.State.Catalogs.TryGetValue(backend, out var catalogState) &&
                !string.IsNullOrWhiteSpace(catalogState.Identity))
            {
                identity = catalogState.Identity;
            }

            bool saved = await _apiService.SaveReasoningSettingsAsync(effort, model, identity);
            if (!saved)
            {
                _selectedReasoningEffort = previousEffort;
                ChatReasoningLabel.Text = previousEffort;
                AutomationProperties.SetName(ChatReasoningButton, $"Reasoning effort: {previousEffort} (Save failed)");
            }
        }

        private void ShowModelSaveFailure()
        {
            ChatModelSaveStatusText.Text = "Could not save selected model.";
            ChatModelSaveStatusText.Visibility = Visibility.Visible;
            _modelSaveFailureTimer ??= new DispatcherTimer { Interval = Services.UiTiming.ModelSaveFailure };
            _modelSaveFailureTimer.Stop();
            _modelSaveFailureTimer.Tick -= OnModelSaveFailureTimerTick;
            _modelSaveFailureTimer.Tick += OnModelSaveFailureTimerTick;
            _modelSaveFailureTimer.Start();
        }

        private void OnModelSaveFailureTimerTick(object? sender, object e)
        {
            _modelSaveFailureTimer?.Stop();
            ChatModelSaveStatusText.Visibility = Visibility.Collapsed;
            ChatModelSaveStatusText.Text = string.Empty;
        }

        private int? GetCurrentModelContextLength()
        {
            if (_groupedCatalogs is null || string.IsNullOrWhiteSpace(_selectedModel)) return null;
            var state = _groupedCatalogs.State;
            string backend = !string.IsNullOrWhiteSpace(state.SelectedBackend)
                ? state.SelectedBackend
                : _selectedBackend;
            if (string.IsNullOrWhiteSpace(backend)) return null;

            if (state.Catalogs.TryGetValue(backend, out var catalogState))
            {
                string lookup = !string.IsNullOrWhiteSpace(state.SelectedModel)
                    ? state.SelectedModel
                    : _selectedModel;
                var match = catalogState.Catalog.FirstOrDefault(m => string.Equals(m.Id, lookup, StringComparison.OrdinalIgnoreCase));
                if (match?.ContextLength is int len && len > 0)
                {
                    return len;
                }
            }
            return null;
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
            string currentModel = !string.IsNullOrWhiteSpace(_groupedCatalogs?.State.SelectedModel)
                ? _groupedCatalogs.State.SelectedModel
                : _selectedModel;
            int? catalogCtx = GetCurrentModelContextLength();
            var (maxTokens, displayLimit) = ModelContextSpecs.GetContextSpec(currentModel, catalogCtx);

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

            var (totalChars, totalImages) = AgentContextMeterPolicy.Measure(
                Messages.Select(message => ((string?)message.Content, message.Attachments?.Count ?? 0)),
                draftText,
                PendingAttachments.Count);

            var reportedTotals = Messages
                .Where(message => message.Usage is not null)
                .Select(message => message.Usage!.ResolvedTotal)
                .ToList();
            if (_activeAgentUsage is { } activeUsage)
            {
                reportedTotals.Add(activeUsage.ResolvedTotal);
            }

            int reportedTokens = AgentContextMeterPolicy.MaxReportedTokens(reportedTotals);

            int estimatedTokens = ModelContextSpecs.EstimateTokens(totalChars, totalImages);
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

                ContextUsedLabel.Text = $"{ModelContextSpecs.FormatTokens(usedTokens)} / {meter.DisplayLimit}";
                ContextPercentLabel.Text = $"{percentage:0.#}%";
                ContextPercentLabel.Foreground = meter.Severity == AgentContextMeterSeverity.Normal
                    ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"]
                    : brush;
            }
            else
            {
                ContextProgressRing.Value = 0;
                PaintContextBreakdown(estimatedTokens, overheadTokens, usedTokens, meter.MaximumTokens);
                ContextUsedLabel.Text = $"{ModelContextSpecs.FormatTokens(usedTokens)} tokens";
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
            _composerDrafts.Save(_activeSessionId, ChatInputTextBox.Text);
            ScheduleContextMeterUpdate();
            if (string.IsNullOrEmpty(ChatInputTextBox.Text))
            {
                return;
            }
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
