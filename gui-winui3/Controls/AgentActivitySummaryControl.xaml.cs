using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls;

/// <summary>Disclosure header only; the existing timeline remains the detail view.</summary>
public sealed partial class AgentActivitySummaryControl : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(ChatMessageItem), typeof(AgentActivitySummaryControl),
        new PropertyMetadata(null, OnMessageChanged));

    public ChatMessageItem? Message
    {
        get => (ChatMessageItem?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public TranscriptTypography Typography => TranscriptTypography.Instance;

    private ChatMessageItem? _observedMessage;
    private ObservableCollection<AgentActivityItem>? _observedActivities;
    private readonly HashSet<AgentActivityItem> _observedItems = new();
    private DispatcherQueueTimer? _refreshTimer;

    public AgentActivitySummaryControl()
    {
        InitializeComponent();
        Loaded += (_, _) => BindMessage();
        Unloaded += (_, _) => StopObserving();
        ActualThemeChanged += (_, _) => ScheduleRefresh();
    }

    private static void OnMessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((AgentActivitySummaryControl)sender).BindMessage();

    private void BindMessage()
    {
        StopObserving();
        if (IsLoaded && Message is { } message)
        {
            _observedMessage = message;
            message.PropertyChanged += OnMessagePropertyChanged;
            // Detailed mode pays no per-row subscription or summary scan cost.
            if (message.CompactAgentActivity && message.AgentActivities is { } activities)
            {
                _observedActivities = activities;
                activities.CollectionChanged += OnActivitiesChanged;
                foreach (var item in activities) Observe(item);
            }
        }
        RenderSummary();
    }

    private void StopObserving()
    {
        _refreshTimer?.Stop();
        if (_observedMessage is not null) _observedMessage.PropertyChanged -= OnMessagePropertyChanged;
        if (_observedActivities is not null) _observedActivities.CollectionChanged -= OnActivitiesChanged;
        foreach (var item in _observedItems) item.PropertyChanged -= OnActivityPropertyChanged;
        _observedItems.Clear();
        _observedMessage = null;
        _observedActivities = null;
    }

    private void Observe(AgentActivityItem item)
    {
        if (_observedItems.Add(item)) item.PropertyChanged += OnActivityPropertyChanged;
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(ChatMessageItem.AgentActivities) or nameof(ChatMessageItem.CompactAgentActivity))
            BindMessage();
        else if (args.PropertyName == nameof(ChatMessageItem.IsActivityDetailsExpanded)) RenderSummary();
        else if (args.PropertyName is nameof(ChatMessageItem.IsStreaming) or nameof(ChatMessageItem.Role)) ScheduleRefresh();
    }

    private void OnActivitiesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            BindMessage();
            return;
        }
        if (args.OldItems is { } removed)
            foreach (AgentActivityItem item in removed)
                if (_observedActivities?.Contains(item) != true && _observedItems.Remove(item))
                    item.PropertyChanged -= OnActivityPropertyChanged;
        if (args.NewItems is { } added)
            foreach (AgentActivityItem item in added) Observe(item);
        ScheduleRefresh();
    }

    private void OnActivityPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AgentActivityItem.State) or nameof(AgentActivityItem.Kind)
            or nameof(AgentActivityItem.DisplayText)) ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        if (!IsLoaded || Message?.CompactAgentActivity != true) return;
        if (_refreshTimer is null)
        {
            _refreshTimer = DispatcherQueue.CreateTimer();
            _refreshTimer.Interval = UiTiming.ActivityFlushBatch;
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer.Stop();
                RenderSummary();
            };
        }
        if (!_refreshTimer.IsRunning) _refreshTimer.Start();
    }

    private void RenderSummary()
    {
        var summary = Message?.CompactAgentActivity == true
            ? AgentActivitySummaryProjection.ForMessage(Message) : AgentActivitySummary.Empty;
        Visibility = summary.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (summary.Count == 0) return;
        SummaryText.Text = summary.Label;
        SummaryText.Foreground = (Brush)Application.Current.Resources[
            ActivityRowTone.BrushKeyFor(summary.Failed > 0, summary.Waiting > 0)];
        var expanded = Message!.IsActivityDetailsExpanded;
        ExpandIcon.Glyph = expanded ? "\uE70E" : "\uE70D";
        var action = expanded ? "Hide activity details" : "Show activity details";
        AutomationProperties.SetName(SummaryButton, $"{summary.Label}. {action}");
        AutomationProperties.SetItemStatus(SummaryButton, expanded ? "Expanded" : "Collapsed");
        ToolTipService.SetToolTip(SummaryButton, $"{summary.Label}\n{action}");
    }

    private void OnExpandClick(object sender, RoutedEventArgs args)
    {
        if (Message is { } message) message.IsActivityDetailsExpanded = !message.IsActivityDetailsExpanded;
    }
}
