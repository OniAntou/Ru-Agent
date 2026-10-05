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
    public sealed class SessionOpenedEventArgs : EventArgs
    {
        public SessionOpenedEventArgs(string sessionId, string title)
        {
            SessionId = sessionId;
            Title = title;
        }

        public string SessionId { get; }
        public string Title { get; }
    }

    public sealed class ChatMessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? UserTemplate { get; set; }
        public DataTemplate? AssistantTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is ChatMessageItem msg)
            {
                if (msg.Role == "user") return UserTemplate;
                return AssistantTemplate;
            }
            return base.SelectTemplateCore(item, container);
        }
    }

    public sealed class AssistantSegmentTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? TextTemplate { get; set; }
        public DataTemplate? ActivityTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is TextSegment) return TextTemplate;
            if (item is ActivitySegment) return ActivityTemplate;
            return base.SelectTemplateCore(item, container);
        }
    }

    public sealed partial class ChatView : UserControl, IDisposable
    {
                private const double ReadingColumnMaxWidth = 820;
        private const double MessageColumnMaxWidth = 820;
        private const double NarrowWidthThreshold = 900;
        private const double MediumWidthThreshold = 1280;
        private const double NarrowHorizontalInset = 24;
        private const double MediumHorizontalInset = 48;
        private const double WideHorizontalInset = 96;
        private const double MinAvailableInputWidth = 160;
        private const int MaxImageAttachmentBytes = Services.ChatAttachmentPolicy.MaxImageAttachmentBytes;
        private const int MaxTextAttachmentBytes = Services.ChatAttachmentPolicy.MaxTextAttachmentBytes;

        private ApiService? _apiService;
        private HarnessEventSink? _harnessEvents;
        private string? _harnessScenario;
        private string _activeSessionId = "default";
        public string CurrentSessionId => _activeSessionId;

        /// <summary>
        /// Which open attempt owns the transcript. Bumped when an attempt becomes the
        /// active one; every await inside that attempt re-checks it and stops if a
        /// newer attempt has taken over.
        /// </summary>
        private long _sessionOpenGeneration;
        private CancellationTokenSource? _sessionOpenCts;

        /// <summary>The session <see cref="_sessionOpenGeneration"/> was opened for.</summary>
        private string _sessionOpenGenerationSessionId = string.Empty;

        private IReadOnlyList<TodoItemModel>? _latestTodos;
        private bool _todoPanelExpanded;
        private string _activeSessionTitle = "New Chat";
        private AgentRunController? _agentController;
        private CancellationTokenSource? _agentMonitorCts;
        private readonly AgentSessionRecovery _agentSessionRecovery = new();
        private bool _agentBusySeen;
        private AgentPermissionPreset _agentPreset = AgentPermissionPreset.Standard;
        private string? _agentPresetSessionId;
        private long? _agentPresetVersion;
        private string _agentWorkspace = Environment.CurrentDirectory;
        private string? _defaultAgentWorkspace;
        private string? _requestedWorkspace;
        private bool _agentActionPending;
        private bool _isSendingUserTurn;
        private string? _focusedApprovalId;
        private readonly AgentTurnQueue _queuedTurns = new();
        /// <summary>
        /// The transcript, bound to the chat list. A batch collection so the two
        /// bulk loads (session open, end-of-turn reload) replace hundreds of rows
        /// with one reset notification instead of one notification per row.
        /// </summary>
        public BatchObservableCollection<ChatMessageItem> Messages { get; } = new();
        public ObservableCollection<ImageAttachmentItem> PendingAttachments { get; } = new();
        public ObservableCollection<AgentActivityItem> AgentActivities { get; } = new();
        private readonly Dictionary<string, AgentActivityItem> _agentActivityById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _activitySplitOffsetById = new(StringComparer.Ordinal);
        private int _segmentFrozenLength;

        private ChatMessageItem? _activeAgentAssistantMessage;
        private string? _activeAgentRunId;
        private string? _activeWorkspaceId;
        private bool _activeAgentReplyReceived;
        private bool _activeAgentReplyCompleted;
        private AgentModelUsage? _activeAgentUsage;
        private readonly RetainedContextMeterPresenter _retainedContextMeter = new();
        private readonly HashSet<string> _agentDeltaKeys = new(StringComparer.Ordinal);
        private readonly AgentReplyAccumulator _replyAccumulator = new();
        private readonly AgentReplyRenderScheduler _replyRenderScheduler;
        private readonly object _accumulatorLock = new();

        public ChatView()
        {
            this.InitializeComponent();
            // A size chosen while the transcript is open repaints the rows already on
            // screen; without this only rows created afterwards would pick it up.
            TranscriptTypography.Instance.PropertyChanged += OnTranscriptTypographyChanged;
            _replyRenderScheduler = new AgentReplyRenderScheduler(
                _replyAccumulator,
                callback =>
                {
                    if (!DispatcherQueue.TryEnqueue(() => callback()))
                    {
                        throw new OperationCanceledException("The chat dispatcher rejected a render callback.");
                    }
                },
                ApplyReplySnapshot);
            ChatListView.ItemsSource = Messages;
            AttachmentPreviewList.ItemsSource = PendingAttachments;
            QueuedTurnsList.ItemsSource = _queuedTurns.Items;
            _queuedTurns.Items.CollectionChanged += OnQueuedTurnsChanged;
            Messages.CollectionChanged += OnMessagesChanged;
            RootGrid.SizeChanged += OnRootGridSizeChanged;
            ChatListView.ContainerContentChanging += OnChatContainerContentChanging;
            this.Loaded += OnChatViewLoaded;
            InitializeChatHoverEffects();
            UpdateViewMode();
        }

        private void OnQueuedTurnsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            => UpdateQueuedTurnsPanel();

        private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            => UpdateViewMode();

        private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyInputWidth();
        }

        private void OnChatViewLoaded(object sender, RoutedEventArgs e)
        {
            ApplyInputWidth();
            EnsureNewMessagesPillWired();
        }

        private void InitializeChatHoverEffects()
        {
            if (AttachImageButton != null)
            {
                AttachImageButton.PointerEntered += OnAttachImagePointerEntered;
                AttachImageButton.PointerExited += OnAttachImagePointerExited;
            }

            if (ContextMeterButton != null)
            {
                ContextMeterButton.PointerEntered += OnContextMeterPointerEntered;
                ContextMeterButton.PointerExited += OnContextMeterPointerExited;
            }
        }

        private void OnAttachImagePointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (AttachImageButton is null) return;
            AttachImageButton.Background = (Brush)Application.Current.Resources["RuCanvasBrush"];
            if (AttachImageIcon != null) AttachImageIcon.Foreground = (Brush)Application.Current.Resources["RuInkBrush"];
        }

        private void OnAttachImagePointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (AttachImageButton is null) return;
            AttachImageButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (AttachImageIcon != null) AttachImageIcon.Foreground = (Brush)Application.Current.Resources["RuMutedBrush"];
        }

        private DispatcherTimer? _contextHoverTimer;

        private void OnContextMeterPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (ContextMeterButton is null) return;
            var canvas = (Brush)Application.Current.Resources["RuCanvasBrush"];
            ContextMeterButton.Background = canvas;
            _contextHoverTimer?.Stop();
            _contextHoverTimer?.Tick -= OnContextHoverTimerTick;
            _contextHoverTimer = new DispatcherTimer
            {
                Interval = Services.UiTiming.ChatFlash
            };
            _contextHoverTimer.Tick += OnContextHoverTimerTick;
            _contextHoverTimer.Start();
        }

        private void OnContextHoverTimerTick(object? sender, object e)
        {
            _contextHoverTimer?.Stop();
            if (ContextWindowFlyout != null && !ContextWindowFlyout.IsOpen && ContextMeterButton != null)
            {
                try
                {
                    ContextWindowFlyout.ShowAt(ContextMeterButton, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
                    {
                        ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.TransientWithDismissOnPointerMoveAway
                    });
                }
                catch
                {
                    ContextWindowFlyout.ShowAt(ContextMeterButton);
                }
            }
        }

        private void OnContextMeterPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (ContextMeterButton is null) return;
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ContextMeterButton.Background = transparent;
            _contextHoverTimer?.Stop();
        }

        private void UpdateViewMode()
        {
            bool hasMessages = Messages.Count > 0;
            if (WelcomeHeroPanel != null)
            {
                WelcomeHeroPanel.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
            }
            if (ChatContentGrid != null)
            {
                ChatContentGrid.Visibility = hasMessages ? Visibility.Visible : Visibility.Collapsed;
            }
            RefreshBackdropEffect();
        }

        /// <summary>
        /// Re-evaluates the welcome decoration after a theme switch. Meteor
        /// Shower themes animate the meteor field, Starry Night shows the
        /// painting; plain Light/Dark stay still.
        /// </summary>
        public void RefreshBackdropEffect()
        {
            bool show = Messages.Count == 0;
            var theme = MainWindow.CurrentTheme;
            if (MeteorShowerField != null)
            {
                MeteorShowerField.IsActive = show
                    && theme is UiTheme.LightMeteorShower or UiTheme.DarkMeteorShower;
            }
            bool starry = show && theme == UiTheme.StarryNight;
            if (StarryPainting != null)
            {
                StarryPainting.Visibility = starry ? Visibility.Visible : Visibility.Collapsed;
            }
            if (StarryScrim != null)
            {
                StarryScrim.Visibility = starry ? Visibility.Visible : Visibility.Collapsed;
            }
            if (StarryLivingField != null)
            {
                StarryLivingField.IsActive = starry;
            }
        }

        private static bool IsMeteorShowerTheme =>
            MainWindow.CurrentTheme is UiTheme.LightMeteorShower or UiTheme.DarkMeteorShower;

        public event EventHandler<SessionOpenedEventArgs>? SessionOpened;

        private ProviderCatalogController? _providerCatalogController;
        private bool _ownsProviderCatalogController;
        private GroupedProviderCatalogController? _groupedCatalogs;
        private bool _ownsGroupedCatalogs;
        private string _selectedModel = string.Empty;
        private string _selectedBackend = string.Empty;

        /// <summary>
        /// The last settings payload read from the backend, used to name the provider
        /// that is missing a key without a second request while reporting a failure.
        /// </summary>
        private AppSettingsDto? _currentSettings;
        private string _selectedReasoningEffort = "default";
        private DispatcherTimer? _modelSaveFailureTimer;

        public void Initialize(
            ApiService apiService,
            HarnessEventSink? harnessEvents = null,
            string? harnessScenario = null,
            AgentRunController? agentController = null,
            string? defaultAgentWorkspace = null,
            ProviderCatalogController? providerCatalogController = null,
            GroupedProviderCatalogController? groupedProviderCatalogs = null)
        {
            _apiService = apiService;
            _harnessEvents = harnessEvents;
            _harnessScenario = harnessScenario;
            _agentController = agentController;
            string defaultWorkspace = string.IsNullOrWhiteSpace(defaultAgentWorkspace)
                ? Environment.CurrentDirectory
                : defaultAgentWorkspace.Trim();
            _defaultAgentWorkspace = defaultWorkspace;
            _agentWorkspace = defaultWorkspace;
            _providerCatalogController = providerCatalogController ?? new ProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsProviderCatalogController = providerCatalogController == null;
            _groupedCatalogs = groupedProviderCatalogs ?? new GroupedProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsGroupedCatalogs = groupedProviderCatalogs == null;
            _providerCatalogController.StateChanged += OnProviderCatalogStateChanged;
            _groupedCatalogs.StateChanged += OnGroupedStateChanged;
            _agentController?.PropertyChanged += OnAgentControllerChanged;
            _agentController?.ActivityReceived += OnAgentActivityReceived;
            _agentController?.SummaryReceived += OnAgentSummaryReceived;
            _agentController?.AssistantDeltaReceived += OnAgentAssistantDeltaReceived;
            _agentController?.ToolOutputReceived += OnAgentToolOutputReceived;
            _agentController?.UsageReceived += OnAgentUsageReceived;
            _agentController?.ContextCompacted += OnAgentContextCompacted;
            _agentController?.RunAdopted += OnAgentRunAdopted;
            ApprovalPanel.DecisionRequested += OnApprovalDecisionRequested;
            UpdateAgentComposerVisuals();
            UpdateAgentInlineState();

            ChatInputTextBox.TextChanged += OnChatInputTyping;

            Messages.CollectionChanged += (_, __) => UpdateContextUsage();
            PendingAttachments.CollectionChanged += (_, __) => UpdateContextUsage();

            _ = LoadChatModelsAsync();
        }

        public void ReinitializeBackend(
            ApiService apiService,
            AgentRunController? agentController,
            ProviderCatalogController? providerCatalogController,
            GroupedProviderCatalogController? groupedProviderCatalogs)
        {
            _apiService = apiService;
            if (_providerCatalogController != null)
            {
                _providerCatalogController.StateChanged -= OnProviderCatalogStateChanged;
            }
            if (_groupedCatalogs != null)
            {
                _groupedCatalogs.StateChanged -= OnGroupedStateChanged;
            }
            if (_agentController != null)
            {
                _agentController.PropertyChanged -= OnAgentControllerChanged;
                _agentController.ActivityReceived -= OnAgentActivityReceived;
                _agentController.SummaryReceived -= OnAgentSummaryReceived;
                _agentController.AssistantDeltaReceived -= OnAgentAssistantDeltaReceived;
                _agentController.ToolOutputReceived -= OnAgentToolOutputReceived;
                _agentController.UsageReceived -= OnAgentUsageReceived;
                _agentController.ContextCompacted -= OnAgentContextCompacted;
                _agentController.RunAdopted -= OnAgentRunAdopted;
            }

            _agentController = agentController;
            _providerCatalogController = providerCatalogController ?? new ProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsProviderCatalogController = providerCatalogController == null;
            _groupedCatalogs = groupedProviderCatalogs ?? new GroupedProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsGroupedCatalogs = groupedProviderCatalogs == null;
            _providerCatalogController.StateChanged += OnProviderCatalogStateChanged;
            _groupedCatalogs.StateChanged += OnGroupedStateChanged;

            if (_agentController != null)
            {
                _agentController.PropertyChanged += OnAgentControllerChanged;
                _agentController.ActivityReceived += OnAgentActivityReceived;
                _agentController.SummaryReceived += OnAgentSummaryReceived;
                _agentController.AssistantDeltaReceived += OnAgentAssistantDeltaReceived;
                _agentController.ToolOutputReceived += OnAgentToolOutputReceived;
                _agentController.UsageReceived += OnAgentUsageReceived;
                _agentController.ContextCompacted += OnAgentContextCompacted;
                _agentController.RunAdopted += OnAgentRunAdopted;
            }

            UpdateAgentComposerVisuals();
            UpdateAgentInlineState();
            _ = LoadChatModelsAsync();
        }
    }
}
