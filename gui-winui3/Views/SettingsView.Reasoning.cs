using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class SettingsView
{
    private ProviderCatalogController? _providerCatalogController;
    private ProviderCatalogState _providerCatalogState = ProviderCatalogState.Empty(string.Empty);
    private bool _loadingReasoning;
    private DispatcherTimer? _reasoningSaveFailureTimer;

    public async Task RefreshProviderCatalogAsync()
    {
        if (_providerCatalogController is not null)
        {
            await _providerCatalogController.RefreshAsync(force: true);
        }
    }

    private void OnProviderCatalogStateChanged(object? sender, ProviderCatalogState state)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (_providerCatalogController is null ||
                !ReferenceEquals(_providerCatalogController.State, state))
            {
                return;
            }

            ApplyProviderCatalogState(state);
        })) return;
    }

    private void ApplyProviderCatalogState(ProviderCatalogState state)
    {
        _providerCatalogState = state;
        _loadingReasoning = true;
        try
        {
            var options = ProviderReasoningOptions.For(state);
            ReasoningEffortComboBox.Items.Clear();
            foreach (var option in options)
            {
                AddReasoningItem(option.Value, option.Label, option.IsEnabled);
            }

            string selected = string.IsNullOrWhiteSpace(state.SelectedEffort) ? "default" : state.SelectedEffort;
            if (!ReasoningEffortComboBox.Items.Cast<ComboBoxItem>().Any(item =>
                string.Equals(item.Tag as string, selected, StringComparison.Ordinal)))
            {
                AddReasoningItem(selected, $"Unavailable: {selected}", enabled: false);
            }

            ReasoningEffortComboBox.SelectedItem = ReasoningEffortComboBox.Items
                .Cast<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, selected, StringComparison.Ordinal));

            // Model-aware enable: only when catalog ok, not stale, and model actually exposes levels/toggle
            bool hasModelLevels = options.Count > 1;
            bool canSelect = state.IsSuccess && !state.Stale && hasModelLevels;
            ReasoningEffortComboBox.IsEnabled = canSelect;
            if (!canSelect && state.IsSuccess)
            {
                ReasoningSaveStatusText.Text = state.Stale
                    ? "Catalog đang stale — tạm dùng Provider default, sẽ cập nhật sau khi refresh."
                    : "Model này không hỗ trợ reasoning riêng, dùng Provider default.";
                ReasoningSaveStatusText.Visibility = Visibility.Visible;
                _reasoningSaveFailureTimer?.Stop();
            }
            else if (canSelect)
            {
                // Clear stale hint when model supports reasoning; keep failure hint if still visible within 5s timer
                if (ReasoningSaveStatusText.Text.StartsWith("Model này") || ReasoningSaveStatusText.Text.StartsWith("Catalog đang"))
                {
                    ReasoningSaveStatusText.Visibility = Visibility.Collapsed;
                    ReasoningSaveStatusText.Text = string.Empty;
                }
            }
        }
        finally
        {
            _loadingReasoning = false;
        }
    }

    private void AddReasoningItem(string tag, string content, bool enabled)
    {
        ReasoningEffortComboBox.Items.Add(new ComboBoxItem
        {
            Tag = tag,
            Content = content,
            IsEnabled = enabled,
        });
    }

    private async void OnReasoningEffortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingReasoning || _apiService is null ||
            ReasoningEffortComboBox.SelectedItem is not ComboBoxItem item ||
            item.IsEnabled == false)
            return;

        string? model = _providerCatalogState.SelectedModel;
        string? identity = _providerCatalogState.Identity;
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(identity)) return;

        string effort = item.Tag as string ?? "default";
        _loadingReasoning = true;
        try
        {
            bool ok = await _apiService.SaveReasoningSettingsAsync(effort, model, identity);
            if (!ok)
            {
                ApplyProviderCatalogState(_providerCatalogState);
                ShowReasoningSaveFailure();
            }
        }
        finally
        {
            _loadingReasoning = false;
        }
    }

    private void ShowReasoningSaveFailure()
    {
        ReasoningSaveStatusText.Text = "Không thể lưu mức Reasoning; lựa chọn đã được khôi phục.";
        ReasoningSaveStatusText.Visibility = Visibility.Visible;
        _reasoningSaveFailureTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _reasoningSaveFailureTimer.Stop();
        _reasoningSaveFailureTimer.Tick -= OnReasoningSaveFailureTimerTick;
        _reasoningSaveFailureTimer.Tick += OnReasoningSaveFailureTimerTick;
        _reasoningSaveFailureTimer.Start();
    }

    private void OnReasoningSaveFailureTimerTick(object? sender, object e)
    {
        _reasoningSaveFailureTimer?.Stop();
        ReasoningSaveStatusText.Visibility = Visibility.Collapsed;
        ReasoningSaveStatusText.Text = string.Empty;
    }
}
