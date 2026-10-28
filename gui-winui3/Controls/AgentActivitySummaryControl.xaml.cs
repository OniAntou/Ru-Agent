using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls;

/// <summary>
/// The compact activity line: disclosure header only; the existing timeline remains the
/// detail view. See <see cref="AgentActivitySummaryProjection"/> for its wording.
/// </summary>
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
        Unloaded += (_, _) =>
        {
            StopObserving();
            StopLive();
        };
        SummaryButton.PointerEntered += OnPointerEntered;
        SummaryButton.PointerExited += OnPointerExited;
        SummaryButton.PointerCanceled += OnPointerExited;
        SummaryButton.GotFocus += OnGotFocus;
        SummaryButton.LostFocus += OnLostFocus;
        ActualThemeChanged += (_, _) =>
        {
            // The shimmer's colours were read from the old theme.
            StopLive();
            ScheduleRefresh();
        };
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
        if (summary.Count == 0)
        {
            StopLive();
            return;
        }

        // A live title that changes faster than it can be read flickers; hold each
        // one for a moment and catch up on the next refresh.
        var now = Environment.TickCount64;
        if (summary.IsLive && _shownTitle is not null && summary.Title != _shownTitle
            && now - _titleShownAt < TitleMinimumMs)
        {
            ScheduleRefresh();
            summary = summary with { Title = _shownTitle };
        }
        else if (summary.Title != _shownTitle)
        {
            _shownTitle = summary.Title;
            _titleShownAt = now;
        }

        _summary = summary;
        TitleText.Text = summary.Title;
        LeadingIcon.Glyph = summary.Glyph;
        ApplyMeta();
        ApplyTone();
        if (summary.IsLive) StartLive();
        else StopLive();

        var expanded = Message!.IsActivityDetailsExpanded;
        ChevronIcon.Glyph = expanded ? "\uE70E" : "\uE70D";
        ApplyLeadingState();
        var action = expanded ? "Hide activity details" : "Show activity details";
        AutomationProperties.SetName(SummaryButton, $"{summary.Label}. {action}");
        AutomationProperties.SetItemStatus(SummaryButton, expanded ? "Expanded" : "Collapsed");
        ToolTipService.SetToolTip(SummaryButton, $"{summary.Label}\n{action}");
    }

    private const long TitleMinimumMs = 150;
    private AgentActivitySummary _summary = AgentActivitySummary.Empty;
    private string? _shownTitle;
    private long _titleShownAt;
    private bool _pointerOver;
    private bool _focused;
    private DispatcherQueueTimer? _clock;
    private Storyboard? _shimmer;

    /// <summary>The tail: elapsed time while live, then any failures or waits.</summary>
    private void ApplyMeta()
    {
        var parts = new List<string>();
        if (_summary.IsLive && Message is { } message)
            parts.Add(AgentActivitySummaryProjection.Elapsed(DateTime.Now - message.Timestamp));
        if (_summary.Meta.Length > 0) parts.Add(_summary.Meta);
        MetaText.Text = parts.Count == 0 ? string.Empty : "· " + string.Join(" · ", parts);
        // Monospace only for the ticking clock, so its digits do not jitter.
        MetaText.FontFamily = (FontFamily)Application.Current.Resources[
            _summary.IsLive ? "RuMonoFontFamily" : "RuUiFontFamily"];
        MetaText.Visibility = parts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyTone()
    {
        var key = _summary.Tone switch
        {
            AgentActivitySummaryTone.Failed => "RuDangerBrush",
            AgentActivitySummaryTone.Waiting => "RuWarningBrush",
            _ => _pointerOver || _focused ? "RuInkBrush" : "RuMutedBrush",
        };
        var brush = (Brush)Application.Current.Resources[key];
        LeadingIcon.Foreground = brush;
        ChevronIcon.Foreground = brush;
        MetaText.Foreground = _summary.Tone == AgentActivitySummaryTone.Quiet
            ? (Brush)Application.Current.Resources["RuMutedBrush"] : brush;
        // The shimmer owns the title's brush while the turn runs.
        if (_shimmer is null) TitleText.Foreground = brush;
    }

    /// <summary>The kind glyph gives way to the chevron on hover, focus or while open.</summary>
    private void ApplyLeadingState()
    {
        var chevron = _pointerOver || _focused || Message?.IsActivityDetailsExpanded == true;
        LeadingIcon.Opacity = chevron ? 0 : 1;
        ChevronIcon.Opacity = chevron ? 1 : 0;
    }

    private void StartLive()
    {
        if (_clock is null)
        {
            _clock = DispatcherQueue.CreateTimer();
            _clock.Interval = TimeSpan.FromSeconds(1);
            _clock.Tick += (_, _) => ApplyMeta();
        }
        if (!_clock.IsRunning) _clock.Start();
        if (_shimmer is null && UiTiming.AnimationsEnabled) StartShimmer();
    }

    private void StopLive()
    {
        _clock?.Stop();
        if (_shimmer is null) return;
        _shimmer.Stop();
        _shimmer = null;
        ApplyTone();
    }

    /// <summary>A light band sweeping across the muted title while the turn runs.</summary>
    private void StartShimmer()
    {
        var muted = ((SolidColorBrush)Application.Current.Resources["RuMutedBrush"]).Color;
        var ink = ((SolidColorBrush)Application.Current.Resources["RuInkBrush"]).Color;
        var sweep = new TranslateTransform { X = -1 };
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
            RelativeTransform = sweep,
            GradientStops =
            {
                new GradientStop { Color = muted, Offset = 0.3 },
                new GradientStop { Color = ink, Offset = 0.5 },
                new GradientStop { Color = muted, Offset = 0.7 },
            },
        };
        var animation = new DoubleAnimation
        {
            From = -1,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(1600),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, sweep);
        Storyboard.SetTargetProperty(animation, "X");
        _shimmer = new Storyboard();
        _shimmer.Children.Add(animation);
        TitleText.Foreground = brush;
        _shimmer.Begin();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => SetHover(pointer: true, focus: _focused);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => SetHover(pointer: false, focus: _focused);

    private void OnGotFocus(object sender, RoutedEventArgs e) => SetHover(pointer: _pointerOver, focus: SummaryButton.FocusState == FocusState.Keyboard);

    private void OnLostFocus(object sender, RoutedEventArgs e) => SetHover(pointer: _pointerOver, focus: false);

    private void SetHover(bool pointer, bool focus)
    {
        _pointerOver = pointer;
        _focused = focus;
        ApplyLeadingState();
        ApplyTone();
    }

    private void OnExpandClick(object sender, RoutedEventArgs args)
    {
        if (Message is { } message) message.IsActivityDetailsExpanded = !message.IsActivityDetailsExpanded;
    }
}
