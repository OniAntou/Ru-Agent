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

    public sealed class SessionStartedEventArgs(string sessionId, string prompt) : EventArgs
    {
        public string SessionId { get; } = sessionId;
        public string Prompt { get; } = prompt;
    }

    /// <summary>
    /// Raised when opening a session adopts its stored permission preset, so
    /// the host can reflect the durable choice in its own preset surfaces.
    /// </summary>
    public sealed class SessionPresetAdoptedEventArgs : EventArgs
    {
        public SessionPresetAdoptedEventArgs(string sessionId, AgentPermissionPreset preset)
        {
            SessionId = sessionId;
            Preset = preset;
        }

        public string SessionId { get; }
        public AgentPermissionPreset Preset { get; }
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
        // Cover both 16px composer margins even at the narrow breakpoint.
        private const double NarrowHorizontalInset = 32;
        private const double MediumHorizontalInset = 48;
        private const double WideHorizontalInset = 96;
        private const double MinAvailableInputWidth = 160;
        private const int MaxImageAttachmentBytes = Services.ChatAttachmentPolicy.MaxImageAttachmentBytes;
        private const int MaxTextAttachmentBytes = Services.ChatAttachmentPolicy.MaxTextAttachmentBytes;

        private ApiService? _apiService;
        private HarnessEventSink? _harnessEvents;
        private string? _harnessScenario;
        private readonly ChatSessionPresentation _sessionPresentation = new();
        private string _activeSessionId => _sessionPresentation.SessionId;
        public string CurrentSessionId => _activeSessionId;

        private long _sessionOpenGeneration => _sessionPresentation.Generation;

        private IReadOnlyList<TodoItemModel>? _latestTodos;
        private bool _todoPanelExpanded;
        private string _activeSessionTitle = "New Chat";
        private AgentRunController? _agentController;
        private readonly AgentSessionRecovery _agentSessionRecovery = new();
        private bool _agentBusySeen;
        private string _agentWorkspace = Environment.CurrentDirectory;
        private string? _defaultAgentWorkspace;
        private string? _requestedWorkspace;
        /// <summary>Last approved identity hash per workspace path; drives the re-trust copy only.</summary>
        private readonly WorkspaceTrustMemoryStore _workspaceTrustMemory = new();
        private readonly SessionHistoryPresenter _sessionHistoryPresenter = new();
        private bool _agentActionPending => _sessionHistoryPresenter.IsActionPending;
        private bool _isSendingUserTurn;
        /// <summary>
        /// The transcript, bound to the chat list. A batch collection so the two
        /// bulk loads (session open, end-of-turn reload) replace hundreds of rows
        /// with one reset notification instead of one notification per row.
        /// </summary>
        public BatchObservableCollection<ChatMessageItem> Messages { get; } = new();
        /// <summary>The composer's unsent content: attachments, per-session drafts and the turn queue.</summary>
        public ChatComposerViewModel Composer { get; }

        /// <summary>The session's permission preset and the Host's stored version of it.</summary>
        public SessionPresetViewModel Presets { get; }
        public ObservableCollection<AgentActivityItem> AgentActivities { get; } = new();
        private readonly AgentActivityFeedPresenter _agentActivityFeed = new();
        private readonly Dictionary<string, int> _activitySplitOffsetById = new(StringComparer.Ordinal);
        private int _segmentFrozenLength;

        private ChatMessageItem? _activeAgentAssistantMessage;
        private string? _activeAgentRunId;
        private string? _activeWorkspaceId;
        private bool _activeAgentReplyReceived;
        private AgentModelUsage? _activeAgentUsage;
        private readonly RetainedContextMeterPresenter _retainedContextMeter = new();
        private readonly AgentReplyAccumulator _replyAccumulator = new();
        private readonly AgentReplyRenderScheduler _replyRenderScheduler;
        private readonly object _accumulatorLock = new();

        public ChatView()
        {
            ModelSelection = new ChatModelPickerViewModel(action =>
            {
                if (!DispatcherQueue.TryEnqueue(() => action()))
                {
                    GuiDiagnosticWriter.AppendTraceLine("[ChatView] dispatcher rejected a model selection update.");
                }
            });
            ModelSelection.PickerProjectionReady += OnModelSelectionPickerReady;
            ModelSelection.SelectionChanged += OnModelSelectionChanged;
            Composer = new ChatComposerViewModel(RunPanel);
            Presets = new SessionPresetViewModel(new HostSessionPresetBackend(() => _apiService, () => _agentController));
            Composer.TextReplaced += OnComposerTextReplaced;
            this.InitializeComponent();
            RunPanel.PropertyChanged += OnRunPanelPropertyChanged;
            RunPanel.ApprovalArrived += OnRunPanelApprovalArrived;
            RunPanel.QuestionArrived += OnRunPanelQuestionArrived;
            RunPanel.TrustPromptRequested += OnRunPanelTrustPromptRequested;
            // A size chosen while the transcript is open repaints the rows already on
            // screen; without this only rows created afterwards would pick it up.
            TranscriptTypography.Instance.PropertyChanged += OnTranscriptTypographyChanged;
            RefreshTranscriptTypography();
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
            InitializeChatScrolling();
            AttachmentPreviewList.ItemsSource = Composer.Attachments;
            QueuedTurnsList.ItemsSource = Composer.Queue.Items;
            Composer.Queue.Items.CollectionChanged += OnQueuedTurnsChanged;
            Messages.CollectionChanged += OnMessagesChanged;
            RootGrid.SizeChanged += OnRootGridSizeChanged;
            ChatListView.ContainerContentChanging += OnChatContainerContentChanging;
            this.Loaded += OnChatViewLoaded;
            InitializeChatHoverEffects();
            InitializeSidePanel();
            UpdateViewMode();
        }

        private void OnQueuedTurnsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            => UpdateQueuedTurnsPanel();

        private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            _transcriptRevision++;
            UpdateViewMode();
        }

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
            Services.UiMotion.FadeBrush(AttachImageButton, Control.BackgroundProperty, (Brush)Application.Current.Resources["RuCanvasBrush"]);
            if (AttachImageIcon != null) Services.UiMotion.FadeBrush(AttachImageIcon, IconElement.ForegroundProperty, (Brush)Application.Current.Resources["RuInkBrush"]);
        }

        private void OnAttachImagePointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (AttachImageButton is null) return;
            Services.UiMotion.FadeBrush(AttachImageButton, Control.BackgroundProperty, null);
            if (AttachImageIcon != null) Services.UiMotion.FadeBrush(AttachImageIcon, IconElement.ForegroundProperty, (Brush)Application.Current.Resources["RuMutedBrush"]);
        }

        private DispatcherTimer? _contextHoverTimer;

        private void OnContextMeterPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (ContextMeterButton is null) return;
            var canvas = (Brush)Application.Current.Resources["RuCanvasBrush"];
            Services.UiMotion.FadeBrush(ContextMeterButton, Control.BackgroundProperty, canvas);
            // One timer for the control's lifetime: a fresh DispatcherTimer per
            // hover churned allocations and left the previous Tick handler behind
            // until the next hover.
            if (_contextHoverTimer is null)
            {
                _contextHoverTimer = new DispatcherTimer
                {
                    Interval = Services.UiTiming.ChatFlash
                };
                _contextHoverTimer.Tick += OnContextHoverTimerTick;
            }

            _contextHoverTimer.Stop();
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
            Services.UiMotion.FadeBrush(ContextMeterButton, Control.BackgroundProperty, null);
            _contextHoverTimer?.Stop();
        }

        private void UpdateViewMode()
        {
            UpdateWelcomeHero(Messages.Count > 0);
            ApplySidePanelState();
            RefreshBackdropEffect();
        }

        /// <summary>
        /// Re-evaluates the welcome decoration after a theme switch. The Meteor
        /// Shower theme animates the meteor field; plain Light/Dark stay still.
        /// </summary>
        public void RefreshBackdropEffect()
        {
            bool show = Messages.Count == 0;
            var theme = MainWindow.CurrentTheme;
            if (MeteorShowerField != null)
            {
                MeteorShowerField.IsActive = show && theme == UiTheme.DarkMeteorShower;
            }

        }

        public event EventHandler<SessionOpenedEventArgs>? SessionOpened;

        public event EventHandler<SessionStartedEventArgs>? SessionStarted;

        public event EventHandler<SessionPresetAdoptedEventArgs>? SessionPresetAdopted;

        private ProviderCatalogController? _providerCatalogController;
        private bool _ownsProviderCatalogController;
        private GroupedProviderCatalogController? _groupedCatalogs;
        private bool _ownsGroupedCatalogs;

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
            AttachModelSelection();
            _agentController?.PropertyChanged += OnAgentControllerChanged;
            _agentController?.ActivityReceived += OnAgentActivityReceived;
            _agentController?.SummaryReceived += OnAgentSummaryReceived;
            _agentController?.AssistantDeltaReceived += OnAgentAssistantDeltaReceived;
            _agentController?.ToolOutputReceived += OnAgentToolOutputReceived;
            _agentController?.UsageReceived += OnAgentUsageReceived;
            _agentController?.ContextCompacted += OnAgentContextCompacted;
            if (_agentController is not null) _agentController.ContextEstimateReceived += OnContextEstimateReceived;
            _agentController?.RunAdopted += OnAgentRunAdopted;
            ApprovalPanel.DecisionRequested += OnApprovalDecisionRequested;
            QuestionPanel.AnswerRequested += OnQuestionAnswerRequested;
            UpdateAgentComposerVisuals();
            UpdateAgentInlineState();

            ChatInputTextBox.TextChanged += OnChatInputTyping;

            Messages.CollectionChanged += (_, __) => UpdateContextUsage();
            Composer.Attachments.CollectionChanged += (_, __) => UpdateContextUsage();

            _ = LoadChatModelsAsync();
        }

        public void ReinitializeBackend(
            ApiService apiService,
            AgentRunController? agentController,
            ProviderCatalogController? providerCatalogController,
            GroupedProviderCatalogController? groupedProviderCatalogs)
        {
            _apiService = apiService;
            if (_ownsProviderCatalogController && _providerCatalogController is not null)
            {
                // Replacing a controller this view owns means the old one has no
                // other owner: dispose it (never blocking the UI thread) instead
                // of leaving its requests to the GC. A fault is only logged.
                var ownedCatalog = _providerCatalogController;
                _ = ownedCatalog.DisposeAsync().AsTask().ContinueWith(
                    static faulted => GuiDiagnosticWriter.AppendTraceLine(
                        $"[ChatView] Catalog dispose failed: {faulted!.Exception!.GetBaseException().Message}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
            if (_ownsGroupedCatalogs && _groupedCatalogs is not null)
            {
                var ownedGrouped = _groupedCatalogs;
                _ = ownedGrouped.DisposeAsync().AsTask().ContinueWith(
                    static faulted => GuiDiagnosticWriter.AppendTraceLine(
                        $"[ChatView] Grouped catalog dispose failed: {faulted!.Exception!.GetBaseException().Message}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
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
                _agentController.ContextEstimateReceived -= OnContextEstimateReceived;
                _agentController.RunAdopted -= OnAgentRunAdopted;
            }

            _agentController = agentController;
            _providerCatalogController = providerCatalogController ?? new ProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsProviderCatalogController = providerCatalogController == null;
            _groupedCatalogs = groupedProviderCatalogs ?? new GroupedProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsGroupedCatalogs = groupedProviderCatalogs == null;
            AttachModelSelection();

            if (_agentController != null)
            {
                _agentController.PropertyChanged += OnAgentControllerChanged;
                _agentController.ActivityReceived += OnAgentActivityReceived;
                _agentController.SummaryReceived += OnAgentSummaryReceived;
                _agentController.AssistantDeltaReceived += OnAgentAssistantDeltaReceived;
                _agentController.ToolOutputReceived += OnAgentToolOutputReceived;
                _agentController.UsageReceived += OnAgentUsageReceived;
                _agentController.ContextCompacted += OnAgentContextCompacted;
                _agentController.ContextEstimateReceived += OnContextEstimateReceived;
                _agentController.RunAdopted += OnAgentRunAdopted;
            }

            UpdateAgentComposerVisuals();
            UpdateAgentInlineState();
            _ = LoadChatModelsAsync();
        }
    }
}
