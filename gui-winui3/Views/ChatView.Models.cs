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
            // The tree is only built while the flyout is open (see
            // ApplyGroupedState), so paint the newest state now if background
            // publishes marked it dirty: they skip the rebuild while closed.
            if (_modelPickerDirty && _groupedCatalogs != null)
            {
                ApplyPickerProjection(_groupedCatalogs.State);
            }

            // Due-based, not forced: opening the picker fanned out one forced
            // refresh per child (two hundred concurrent GET /models), which is
            // what made the dropdown lag on open. The selected backend is always
            // refreshed through the single controller; the rest only refetch
            // when their poll interval lapsed.
            _ = RefreshGroupedCatalogsAsync(force: false);
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
        private DispatcherQueueTimer? _groupedPublishCoalesceTimer;
        private GroupedProviderCatalogState? _pendingGroupedState;
        private bool _modelPickerDirty = true;

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

        /// <summary>
        /// Escape, or any other way the list asked to be left without a choice.
        /// </summary>
        /// <remarks>
        /// The flyout hides here rather than in the picker because the picker does not
        /// own it: the Flyout is declared in this view so it can sit on the toolbar
        /// button. Relying on light dismiss alone was not good enough, because a focused
        /// text field is exactly the case where Escape is least certain to reach the
        /// popup, and being unable to back out of a list is worse than a redundant close.
        /// </remarks>
        private void OnModelPickerDismissRequested(object? sender, EventArgs e) =>
            ChatModelFlyout.Hide();

        private void OnGroupedStateChanged(object? sender, GroupedProviderCatalogState state)
        {
            // A poll publishes once per provider; applying each one re-projects
            // all groups on the UI thread. Collapse the burst into one trailing
            // apply of the newest state instead.
            _pendingGroupedState = state;
            if (_groupedPublishCoalesceTimer is null)
            {
                _groupedPublishCoalesceTimer = DispatcherQueue.CreateTimer();
                _groupedPublishCoalesceTimer.Interval = UiTiming.GroupedPublishCoalesce;
                _groupedPublishCoalesceTimer.Tick += OnGroupedPublishCoalesceTick;
            }

            _groupedPublishCoalesceTimer.Stop();
            _groupedPublishCoalesceTimer.Start();
        }

        private void OnGroupedPublishCoalesceTick(object? sender, object e)
        {
            _groupedPublishCoalesceTimer?.Stop();
            var state = _pendingGroupedState;
            _pendingGroupedState = null;
            if (state is null) return;

            if (_groupedCatalogs is null ||
                !ReferenceEquals(_groupedCatalogs.State, state))
            {
                return;
            }

            ApplyGroupedState(state);
        }

        /// <summary>
        /// The single controller lands after the grouped one is still empty (it is
        /// refreshed for the selected backend on load and on every model switch).
        /// When it does, the stored effort and the level list may have arrived, so
        /// recompute the button. Anything for another backend is ignored: the
        /// shared controller is repointed by Settings too.
        /// </summary>
        private void OnProviderCatalogStateChanged(object? sender, ProviderCatalogState state)
        {
            string backend = !string.IsNullOrWhiteSpace(_groupedCatalogs?.State.SelectedBackend)
                ? _groupedCatalogs.State.SelectedBackend
                : _selectedBackend;
            if (!string.Equals(state.Backend, backend, StringComparison.Ordinal)) return;

            if (!DispatcherQueue.TryEnqueue(UpdateReasoningEffortUi))
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
            // The dropdown tree costs a full rebuild per publish; with two
            // hundred providers one poll cycle is hundreds of publishes. While
            // the flyout is closed nobody sees the tree, so skip the build and
            // repaint once on open instead of janking the UI thread in
            // the background. Label, meter and effort below stay live.
            if (ChatModelFlyout.IsOpen)
            {
                ApplyPickerProjection(state);
            }
            else
            {
                _modelPickerDirty = true;
            }

            UpdateContextUsage();
            UpdateReasoningEffortUi();
        }

        /// <summary>
        /// Rebuilds the model dropdown from grouped state. Skipped by the view
        /// itself when the content is unchanged.
        /// </summary>
        private void ApplyPickerProjection(GroupedProviderCatalogState state)
        {
            ModelPicker.Apply(ProviderModelPickerProjection.Create(
                state.Catalogs.Values,
                state.SelectedBackend,
                state.SelectedModel,
                state.SearchText,
                _groupedCatalogs?.CurrentDefinitions));
            _modelPickerDirty = false;
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
                    // Due-based: a forced refresh fanned out one GET /models per
                    // child, stalling the next picker open on two hundred requests.
                    _ = _groupedCatalogs?.RefreshAsync(force: false);
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
            _selectedReasoningEffort = ProviderReasoningOptions.Clamp(
                _selectedReasoningEffort, options, IsCatalogReadyForSelection());

            ChatReasoningLabel.Text = _selectedReasoningEffort;
            AutomationProperties.SetName(ChatReasoningButton, $"Reasoning effort: {_selectedReasoningEffort}");
        }

        /// <summary>
        /// Whether the catalog that would validate the current effort has arrived.
        /// </summary>
        /// <remarks>
        /// Until it does, the option list is just "default" and nothing can be
        /// concluded from it, so the effort is left alone.
        /// </remarks>
        private bool IsCatalogReadyForSelection() =>
            TryGetSelectedCatalog(out _);

        /// <summary>
        /// The catalog for the selected backend, preferring the single controller
        /// this view points at it. The grouped controller holds two hundred
        /// children behind polling and a fan-out fetch, so it is empty at startup
        /// and long after; the single refresh in LoadChatModelsAsync is what has
        /// SelectedEffort, Identity and reasoning levels when the composer needs
        /// them.
        /// </summary>
        private bool TryGetSelectedCatalog(out ProviderCatalogState? catalog)
        {
            string backend = !string.IsNullOrWhiteSpace(_groupedCatalogs?.State.SelectedBackend)
                ? _groupedCatalogs.State.SelectedBackend
                : _selectedBackend;
            return ProviderSelectedCatalog.TryResolve(
                _providerCatalogController?.State,
                _groupedCatalogs?.State.Catalogs,
                backend,
                out catalog);
        }

        /// <summary>
        /// The catalog for the selected backend, or null when none has arrived.
        /// </summary>
        /// <remarks>
        /// The session usage panel prices a session's runs from this. It is exposed as
        /// null rather than as a placeholder catalog because "no rates to price with"
        /// and "priced against nothing" have to stay the same answer: a panel handed an
        /// empty catalog would report every run as free.
        /// </remarks>
        public ProviderCatalogState? ProviderCatalogState =>
            TryGetSelectedCatalog(out var catalog) ? catalog : null;

        private IReadOnlyList<ProviderReasoningOption> GetCurrentReasoningOptions()
        {
            if (TryGetSelectedCatalog(out var catalog) && catalog is not null)
            {
                string effectiveModel = !string.IsNullOrWhiteSpace(_groupedCatalogs?.State.SelectedModel)
                    ? _groupedCatalogs.State.SelectedModel
                    : _selectedModel;
                return ProviderReasoningOptions.For(catalog, effectiveModel);
            }

            return [new ProviderReasoningOption("default", "default")];
        }

        private void SyncEffortFromCatalog(bool force = false)
        {
            if (TryGetSelectedCatalog(out var catalog) && catalog is not null)
            {
                if (!string.IsNullOrWhiteSpace(catalog.SelectedEffort) &&
                    (force ||
                    string.Equals(_selectedReasoningEffort, "default", StringComparison.OrdinalIgnoreCase)))
                {
                    _selectedReasoningEffort = catalog.SelectedEffort;
                }
            }
        }

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
            SyncEffortFromCatalog();
            var options = GetCurrentReasoningOptions();

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
                bool isSelected = string.Equals(option.Value, _selectedReasoningEffort, StringComparison.OrdinalIgnoreCase);
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
                await ApplyReasoningEffortAsync(_reasoningEfforts[index]);
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Reasoning effort selection failed: {ex.Message}");
            }
        }

        private async Task ApplyReasoningEffortAsync(string effort)
        {
            string previousEffort = _selectedReasoningEffort;
            _selectedReasoningEffort = effort;
            ChatReasoningLabel.Text = effort;
            AutomationProperties.SetName(ChatReasoningButton, $"Reasoning effort: {effort}");

            if (_apiService is null) return;

            string backend = !string.IsNullOrWhiteSpace(_groupedCatalogs?.State.SelectedBackend)
                ? _groupedCatalogs.State.SelectedBackend
                : _selectedBackend;
            string model = _selectedModel ?? string.Empty;
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(backend)) return;

            // The save is selection-bound: the backend compares this against the
            // snapshot identity (a credential hash), so the backend string itself
            // is never a valid substitute. The single controller refreshed at load
            // carries the real one; without any catalog the save is honestly
            // rejected and the label below reports it.
            string identity = backend;
            if (TryGetSelectedCatalog(out var catalogState) && catalogState is not null &&
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
            if (!_composerDrafts.TrySave(_activeSessionId, ChatInputTextBox.Text, PendingAttachments))
            {
                ShowAgentInlineStatus(ComposerDraftStore.RetentionLimitStatus);
            }
            else if (_localComposerStatus == ComposerDraftStore.RetentionLimitStatus)
            {
                ClearComposerStatus();
            }
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
