using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls;

public sealed partial class TurnChangesSummaryControl : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(ChatMessageItem), typeof(TurnChangesSummaryControl),
        new PropertyMetadata(null, OnMessageChanged));

    public ChatMessageItem? Message
    {
        get => (ChatMessageItem?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    private ChatMessageItem? _observedMessage;
    private ObservableCollection<AgentActivityItem>? _observedActivities;
    private readonly HashSet<AgentActivityItem> _observedItems = [];
    private TurnChangeSummary _summary = TurnChangeSummary.Empty;
    private bool _expanded;
    private bool _openingDetails;
    private ContentDialog? _detailsDialog;

    public TurnChangesSummaryControl()
    {
        InitializeComponent();
        Loaded += (_, _) => BindMessage();
        Unloaded += (_, _) =>
        {
            StopObserving();
            _detailsDialog?.Hide();
        };
    }

    private static void OnMessageChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var control = (TurnChangesSummaryControl)source;
        control._expanded = false;
        control._detailsDialog?.Hide();
        control.DetailsError.Visibility = Visibility.Collapsed;
        control.BindMessage();
    }

    private void BindMessage()
    {
        StopObserving();
        if (IsLoaded && Message is { } message)
        {
            _observedMessage = message;
            message.PropertyChanged += OnMessagePropertyChanged;
            _observedActivities = message.AgentActivities;
            if (_observedActivities is not null)
                _observedActivities.CollectionChanged += OnActivitiesChanged;
            ObserveSettledItems();
        }
        RenderSummary();
    }

    private void StopObserving()
    {
        if (_observedMessage is not null) _observedMessage.PropertyChanged -= OnMessagePropertyChanged;
        if (_observedActivities is not null) _observedActivities.CollectionChanged -= OnActivitiesChanged;
        foreach (var item in _observedItems) item.PropertyChanged -= OnActivityPropertyChanged;
        _observedItems.Clear();
        _observedMessage = null;
        _observedActivities = null;
    }

    private void ObserveSettledItems()
    {
        foreach (var item in _observedItems) item.PropertyChanged -= OnActivityPropertyChanged;
        _observedItems.Clear();
        // While streaming, only the terminal transition matters. Avoid rebuilding
        // subscriptions and projections on every frame of a growing live feed.
        if (Message?.IsStreaming != false || _observedActivities is null) return;
        foreach (var item in _observedActivities)
            if (_observedItems.Add(item)) item.PropertyChanged += OnActivityPropertyChanged;
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ChatMessageItem.AgentActivities)) BindMessage();
        else if (args.PropertyName is nameof(ChatMessageItem.IsStreaming) or nameof(ChatMessageItem.Role))
        {
            if (Message?.IsStreaming == true) _detailsDialog?.Hide();
            ObserveSettledItems();
            RenderSummary();
        }
    }

    private void OnActivitiesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (Message?.IsStreaming != false) return;
        ObserveSettledItems();
        RenderSummary();
    }

    private void OnActivityPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AgentActivityItem.State) or nameof(AgentActivityItem.Kind)
            or nameof(AgentActivityItem.FileName) or nameof(AgentActivityItem.Added) or nameof(AgentActivityItem.Removed)
            or nameof(AgentActivityItem.Detail) or nameof(AgentActivityItem.LineStart)) RenderSummary();
    }

    private void RenderSummary()
    {
        _summary = Message is { } message ? TurnChangesProjection.ForMessage(message) : TurnChangeSummary.Empty;
        Visibility = _summary.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryTitle.Text = _summary.Title;
        AddedTotal.Text = _summary.AddedText;
        RemovedTotal.Text = _summary.RemovedText;
        ToolTipService.SetToolTip(AddedTotal, _summary.Added is null ? "Added line count unavailable" : "Lines added in this turn");
        ToolTipService.SetToolTip(RemovedTotal, _summary.Removed is null ? "Removed line count unavailable" : "Lines removed in this turn");
        RenderFiles();
    }

    private void RenderFiles()
    {
        FilesList.ItemsSource = _expanded ? _summary.Files : _summary.Files.Take(TurnChangesProjection.PreviewFileCount).ToArray();
        ExpandButton.Visibility = _summary.Files.Count > TurnChangesProjection.PreviewFileCount ? Visibility.Visible : Visibility.Collapsed;
        ExpandLabel.Text = _expanded ? "Show fewer files" : $"Show all {_summary.Files.Count} files";
        ExpandIcon.Glyph = _expanded ? "\uE70E" : "\uE70D";
        AutomationProperties.SetName(ExpandButton, ExpandLabel.Text);
        AutomationProperties.SetItemStatus(ExpandButton, _expanded ? "Expanded" : "Collapsed");
    }

    private void OnExpandClick(object sender, RoutedEventArgs args)
    {
        _expanded = !_expanded;
        RenderFiles();
    }
}
