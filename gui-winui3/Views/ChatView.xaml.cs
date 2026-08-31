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
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using RuAgent.Client;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;

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

    public sealed partial class ChatView : UserControl, IDisposable
    {
        private const string VtsMarkerPattern = @"\s*\[\[(?:VTS|VOICE_PACE):[^\]]*\]\]";
        private const double ReadingColumnMaxWidth = 820;

        private ApiService? _apiService;
        private VoicePlaybackService? _voicePlayback;
        private VoiceTurnCaptureService? _voiceCapture;
        private VoiceConversationService? _voiceConversation;
        private HarnessEventSink? _harnessEvents;
        private string? _harnessScenario;
        private string _activeSessionId = "default";
        private AgentRunController? _agentController;
        private CancellationTokenSource? _agentMonitorCts;
        private readonly AgentSessionRecovery _agentSessionRecovery = new();
        private bool _agentActivityVisible;
        private AgentPermissionPreset _agentPreset = AgentPermissionPreset.Standard;
        private string? _agentPresetSessionId;
        private long? _agentPresetVersion;
        private string? _agentApprovalScopeApprovalId;
        private string _agentWorkspace = Environment.CurrentDirectory;
        private string? _defaultAgentWorkspace;
        private bool _agentActionPending;
        private AgentRunSummaryItem? _agentRunSummary;
        public ObservableCollection<ChatMessageItem> Messages { get; } = new();
        public ObservableCollection<AgentActivityItem> AgentActivities { get; } = new();
        private readonly Dictionary<string, AgentActivityItem> _agentActivityById = new(StringComparer.Ordinal);
        private DispatcherTimer? _typingDebounce;
        private bool _typingPushed;
        private enum AgentTurnSource
        {
            Typed,
            Voice,
        }

        private AgentTurnSource _activeAgentTurnSource;
        private ChatMessageItem? _activeAgentAssistantMessage;
        private string? _activeAgentRunId;
        private bool _activeAgentReplyReceived;
        private bool _activeAgentReplyCompleted;
        private readonly HashSet<string> _agentDeltaKeys = new(StringComparer.Ordinal);
        private readonly AgentReplyAccumulator _replyAccumulator = new();
        private bool _renderPending;
        private readonly object _renderGate = new();

        public ChatView()
        {
            this.InitializeComponent();
            ChatListView.ItemsSource = Messages;
            AgentActivityFeed.ItemsSource = AgentActivities;
            Messages.CollectionChanged += (s, e) => UpdateViewMode();
            RootGrid.SizeChanged += OnRootGridSizeChanged;
            this.Loaded += (s, e) => ApplyInputWidth();
            UpdateViewMode();
        }

        private void UpdateViewMode()
        {
            bool hasMessages = Messages.Count > 0;
            if (WelcomeHeroPanel != null)
            {
                WelcomeHeroPanel.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
            }
            if (ChatScrollHost != null)
            {
                ChatScrollHost.Visibility = hasMessages ? Visibility.Visible : Visibility.Collapsed;
            }
            if (MeteorShowerField != null)
            {
                MeteorShowerField.IsActive = !hasMessages;
            }
        }

        private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyInputWidth();
        }

        public event EventHandler<SessionOpenedEventArgs>? SessionOpened;

        public void Initialize(
            ApiService apiService,
            VoicePlaybackService voicePlayback,
            Func<IVoiceFrameSource>? voiceSourceFactory = null,
            HarnessEventSink? harnessEvents = null,
            string? harnessScenario = null,
            AgentRunController? agentController = null,
            string? defaultAgentWorkspace = null)
        {
            _apiService = apiService;
            _voicePlayback = voicePlayback;
            _harnessEvents = harnessEvents;
            _harnessScenario = harnessScenario;
            _agentController = agentController;
            string defaultWorkspace = string.IsNullOrWhiteSpace(defaultAgentWorkspace)
                ? Environment.CurrentDirectory
                : defaultAgentWorkspace.Trim();
            _defaultAgentWorkspace = defaultWorkspace;
            _agentWorkspace = defaultWorkspace;
            _agentController?.PropertyChanged += OnAgentControllerChanged;
            _agentController?.ActivityReceived += OnAgentActivityReceived;
            _agentController?.SummaryReceived += OnAgentSummaryReceived;
            _agentController?.AssistantDeltaReceived += OnAgentAssistantDeltaReceived;
            UpdateAgentComposerVisuals();
            UpdateAgentInlineState();
            _voiceCapture = new VoiceTurnCaptureService(voiceSourceFactory);
            _voiceConversation = new VoiceConversationService(
                _voiceCapture,
                TranscribeVoiceAsync,
                SendVoiceMessageAsync,
                harnessEvents);
            _voiceConversation.StatusChanged += OnVoiceConversationStatusChanged;

            ChatInputTextBox.TextChanged += OnChatInputTyping;
            _typingDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _typingDebounce.Tick += (_, __) =>
            {
                _typingDebounce.Stop();
                if (!string.IsNullOrEmpty(ChatInputTextBox.Text) && !_typingPushed)
                {
                    _typingPushed = true;
                    _ = _apiService?.PushVtsActivityAsync(true);
                }
            };
        }

        private void OnChatInputTyping(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(ChatInputTextBox.Text))
            {
                _typingDebounce?.Stop();
                if (_typingPushed)
                {
                    _typingPushed = false;
                    _ = _apiService?.PushVtsActivityAsync(false);
                }
                return;
            }
            _typingDebounce?.Stop();
            _typingDebounce?.Start();
        }

        private void OnAgentControllerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateAgentInlineState();
                return;
            }

            DispatcherQueue.TryEnqueue(UpdateAgentInlineState);
        }

        private void OnAgentActivityReceived(object? sender, AgentActivityModel activity)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                ApplyAgentActivity(activity);
                return;
            }

            DispatcherQueue.TryEnqueue(() => ApplyAgentActivity(activity));
        }

        private void ApplyAgentActivity(AgentActivityModel activity)
        {
            if (_agentController?.IsBusy != true
                || string.IsNullOrWhiteSpace(activity.ActivityId))
            {
                return;
            }

            if (_agentActivityById.TryGetValue(activity.ActivityId, out var existing))
            {
                existing.UpdateFrom(activity);
            }
            else
            {
                var item = new AgentActivityItem(activity);
                _agentActivityById.Add(item.ActivityId, item);
                AgentActivities.Add(item);
            }

            AgentActivityFeed.Visibility = Visibility.Visible;
            ScrollChatToBottom();
        }

        private void OnAgentSummaryReceived(object? sender, AgentRunSummaryModel summary)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                ApplyAgentRunSummary(summary);
                return;
            }

            DispatcherQueue.TryEnqueue(() => ApplyAgentRunSummary(summary));
        }

        private void ApplyAgentRunSummary(AgentRunSummaryModel summary)
        {
            if (_agentController?.IsBusy != true)
            {
                return;
            }

            _agentRunSummary = new AgentRunSummaryItem(summary);
            AgentRunSummaryText.Text = _agentRunSummary.DisplayText;
            AgentRunSummaryMetadata.Text = _agentRunSummary.MetadataText;
            AgentRunSummaryGlyph.Glyph = _agentRunSummary.StatusGlyph;
            AgentRunSummaryGlyph.Foreground = _agentRunSummary.IsVerifiedSuccess
                ? (Brush)Application.Current.Resources["RuSuccessBrush"]
                : _agentRunSummary.IsFailure
                    ? (Brush)Application.Current.Resources["AppDangerBrush"]
                    : (Brush)Application.Current.Resources["RuMutedBrush"];
            AgentRunSummaryPanel.Visibility = Visibility.Visible;
            ScrollChatToBottom();
        }

        private void OnAgentAssistantDeltaReceived(object? sender, AgentAssistantDeltaModel delta)
        {
            // Voice delivery must not be gated by render coalescing
            if (_activeAgentTurnSource == AgentTurnSource.Voice
                && _voicePlayback?.AutoReadReplies == true)
            {
                // Deliver voice promptly on any thread that received the delta, but ensure the run is still current
                var runForVoice = _agentController?.CurrentRun;
                if (runForVoice != null && _activeAgentAssistantMessage != null && string.Equals(_activeAgentRunId ?? runForVoice.RunId, runForVoice.RunId, StringComparison.Ordinal))
                {
                    _voicePlayback.AppendReplyDelta(delta.Text);
                }
            }

            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnAgentAssistantDeltaReceived(sender, delta));
                return;
            }

            AgentRunModel? run = _agentController?.CurrentRun;
            if (run is null || _agentController?.IsBusy != true)
            {
                return;
            }

            if (_activeAgentAssistantMessage is null)
            {
                return;
            }

            _activeAgentRunId ??= run.RunId;
            if (!string.Equals(_activeAgentRunId, run.RunId, StringComparison.Ordinal))
            {
                return;
            }

            string key = $"{run.RunId}:{delta.ModelStepId}:{delta.TransientSequence}";
            if (!_agentDeltaKeys.Add(key))
            {
                return;
            }

            // Use accumulator as single authority for dedup + text; also keep legacy set for now
            if (!_replyAccumulator.TryAppend(run.RunId, delta.ModelStepId, delta.TransientSequence, delta.Text))
            {
                // If no active accumulator (e.g., after Invalidate), begin with real runId and retry once
                if (_activeAgentRunId != null && run.RunId == _activeAgentRunId)
                {
                    _replyAccumulator.Begin(_activeSessionId, run.RunId);
                    if (!_replyAccumulator.TryAppend(run.RunId, delta.ModelStepId, delta.TransientSequence, delta.Text))
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            _activeAgentReplyReceived = true;
            ScheduleRender();
        }

        private void ScheduleRender()
        {
            lock (_renderGate)
            {
                if (_renderPending) return;
                _renderPending = true;
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                AgentReplySnapshot? snapshot = null;
                lock (_renderGate)
                {
                    _renderPending = false;
                    snapshot = _replyAccumulator.TakeSnapshot();
                }
                if (snapshot != null && _activeAgentAssistantMessage != null)
                {
                    // Use snapshot text as the single source for Content, coalescing many deltas into one UI update
                    _activeAgentAssistantMessage.Content = snapshot.Text;
                    _activeAgentAssistantMessage.IsStreaming = true;
                    ScrollChatToBottom();
                }
            });
        }

        private void ClearAgentActivities()
        {
            _agentActivityById.Clear();
            AgentActivities.Clear();
            if (AgentActivityFeed is not null)
            {
                AgentActivityFeed.Visibility = Visibility.Collapsed;
            }
        }

        private void ClearAgentRunSummary()
        {
            _agentRunSummary = null;
            if (AgentRunSummaryPanel is not null)
            {
                AgentRunSummaryPanel.Visibility = Visibility.Collapsed;
                AgentRunSummaryText.Text = string.Empty;
                AgentRunSummaryMetadata.Text = string.Empty;
            }
        }

        private void OnAgentPresetChanged(object sender, SelectionChangedEventArgs e)
        {
            _agentPreset = SelectedAgentPreset();
            UpdateAgentInlineState();
        }

        private void ShowAgentInlineStatus(string status)
        {
            _agentActivityVisible = true;
            AgentInlinePanel.Visibility = Visibility.Visible;
            AgentStatusText.Text = status;
        }

        private void UpdateAgentComposerVisuals()
        {
            if (AgentModeLabel is null)
            {
                return;
            }

            AgentModeLabel.Text = "Agent";
            AgentModeIcon.Glyph = "\uEC25";
            AgentPresetBox.Visibility = Visibility.Visible;
            AgentModeButton.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(
                AgentModeButton,
                "Coding Agent");
            ToolTipService.SetToolTip(
                AgentModeButton,
                "Agent · chế độ duy nhất");
            MicButton.IsEnabled = true;
            AutomationProperties.SetName(
                MicButton,
                "Bắt đầu trò chuyện bằng giọng nói");
        }

        private void UpdateAgentInlineState()
        {
            if (_agentController is null)
            {
                AgentInlinePanel.Visibility = _agentActivityVisible ? Visibility.Visible : Visibility.Collapsed;
                if (!_agentActivityVisible)
                {
                    AgentStatusText.Text = string.Empty;
                }
                AgentStopButton.Visibility = Visibility.Collapsed;
                AgentContinueButton.Visibility = Visibility.Collapsed;
                AgentTrustPanel.Visibility = Visibility.Collapsed;
                AgentApprovalPanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (_agentController.CurrentRun is { } currentRun
                && !string.Equals(currentRun.SessionId, _activeSessionId, StringComparison.Ordinal))
            {
                AgentInlinePanel.Visibility = _agentActivityVisible ? Visibility.Visible : Visibility.Collapsed;
                if (!_agentActivityVisible)
                {
                    AgentStatusText.Text = string.Empty;
                }
                AgentStopButton.Visibility = Visibility.Collapsed;
                AgentContinueButton.Visibility = Visibility.Collapsed;
                AgentTrustPanel.Visibility = Visibility.Collapsed;
                AgentApprovalPanel.Visibility = Visibility.Collapsed;
                return;
            }

            bool hasRunState = _agentActivityVisible
                || _agentController.IsBusy
                || _agentController.CanCancel
                || _agentController.HasPendingApproval
                || _agentController.NeedsWorkspaceTrust
                || _agentController.CanContinue;
            AgentInlinePanel.Visibility = hasRunState ? Visibility.Visible : Visibility.Collapsed;
            if (!hasRunState)
            {
                AgentStatusText.Text = string.Empty;
                return;
            }

            AgentStatusText.Text = SafeAgentStatus(_agentController.StatusText);
            AgentStopButton.Visibility = _agentController.CanCancel
                ? Visibility.Visible
                : Visibility.Collapsed;
            AgentContinueButton.Visibility = _agentController.CanContinue
                ? Visibility.Visible
                : Visibility.Collapsed;

            bool trustPending = _agentController.NeedsWorkspaceTrust;
            AgentTrustPanel.Visibility = trustPending ? Visibility.Visible : Visibility.Collapsed;
            if (trustPending && _agentController.CurrentRun is { } run)
            {
                string hash = run.DisplayIdentityHash ?? string.Empty;
                string shortHash = hash.Length > 16 ? hash[..16] + "…" : hash;
                AgentTrustText.Text = $"Đường dẫn: {BoundedDisplay(_agentWorkspace, 260)}\nMã nhận diện: {shortHash}";
            }

            bool approvalPending = _agentController.HasPendingApproval;
            AgentApprovalPanel.Visibility = approvalPending ? Visibility.Visible : Visibility.Collapsed;
            if (approvalPending)
            {
                if (!string.Equals(
                        _agentApprovalScopeApprovalId,
                        _agentController.PendingApprovalId,
                        StringComparison.Ordinal))
                {
                    _agentApprovalScopeApprovalId = _agentController.PendingApprovalId;
                    AgentApprovalScopeBox.SelectedIndex = 0;
                }

                string summary = _agentController.PendingApprovalSummary
                    ?? "Agent đang yêu cầu một hành động cần phê duyệt.";
                bool externalDirectoryApproval = string.Equals(
                    summary,
                    "external_directory_required",
                    StringComparison.Ordinal);
                AgentApprovalSummaryText.Text = externalDirectoryApproval
                    ? "Agent cần truy cập một thư mục ngoài project. Chọn phạm vi quyền trước khi duyệt."
                    : BoundedDisplay(summary, 512);
                AgentApprovalScopePanel.Visibility = externalDirectoryApproval
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (!externalDirectoryApproval)
                {
                    AgentApprovalScopeBox.SelectedIndex = 0;
                }
            }
            else
            {
                _agentApprovalScopeApprovalId = null;
                AgentApprovalScopePanel.Visibility = Visibility.Collapsed;
            }

            bool actionsEnabled = !_agentActionPending;
            AgentTrustApproveButton.IsEnabled = actionsEnabled;
            AgentTrustDenyButton.IsEnabled = actionsEnabled;
            AgentApproveButton.IsEnabled = actionsEnabled;
            AgentDenyButton.IsEnabled = actionsEnabled;
            AgentContinueButton.IsEnabled = actionsEnabled;
            AgentStopButton.IsEnabled = actionsEnabled;
        }

        private static string SafeAgentStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return "Coding Agent sẵn sàng.";
            }

            string trimmed = status.Trim();
            if (trimmed.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            {
                return "Coding Agent gặp lỗi. Mở Công cụ → Nhật ký hoạt động để xem trạng thái.";
            }

            return BoundedDisplay(trimmed, 256);
        }

        private static string BoundedDisplay(string value, int maxLength)
        {
            string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
        }

        private AgentPermissionPreset SelectedAgentPreset()
        {
            if (AgentPresetBox.SelectedItem is ComboBoxItem item
                && string.Equals(item.Tag as string, "full_access", StringComparison.Ordinal))
            {
                return AgentPermissionPreset.FullAccess;
            }

            return AgentPermissionPreset.Standard;
        }

        private bool IsActiveAgentRunPending()
        {
            if (_agentController is null)
            {
                return false;
            }

            AgentRunModel? run = _agentController.CurrentRun;
            return run is not null
                && string.Equals(run.SessionId, _activeSessionId, StringComparison.Ordinal)
                && (_agentController.CanCancel
                    || _agentController.HasPendingApproval
                    || _agentController.NeedsWorkspaceTrust);
        }

        private void ResetAgentState()
        {
            ClearAgentActivities();
            ClearAgentRunSummary();
            if (_agentController?.CanCancel == true)
            {
                _ = _agentController.CancelAsync();
            }

            var monitor = _agentMonitorCts;
            _agentMonitorCts = null;
            monitor?.Cancel();
            monitor?.Dispose();
            _agentActivityVisible = false;
            _agentPreset = AgentPermissionPreset.Standard;
            _agentPresetSessionId = null;
            _agentPresetVersion = null;
            _agentApprovalScopeApprovalId = null;
            _agentActionPending = false;
            AbandonActiveAgentReply(notifyVoice: true);
            if (AgentPresetBox is not null)
            {
                AgentPresetBox.SelectedIndex = 0;
            }

            UpdateAgentComposerVisuals();
            AgentInlinePanel.Visibility = Visibility.Collapsed;
            AgentTrustPanel.Visibility = Visibility.Collapsed;
            AgentApprovalPanel.Visibility = Visibility.Collapsed;
            AgentStopButton.Visibility = Visibility.Collapsed;
            AgentContinueButton.Visibility = Visibility.Collapsed;
            AgentStatusText.Text = string.Empty;
        }


        private static string StripDisplayMarkers(string content)
        {
            return Regex.Replace(content, VtsMarkerPattern, "").Trim();
        }

        private async void OnSuggestionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            var promptText = button.Tag as string;
            if (string.IsNullOrWhiteSpace(promptText))
            {
                return;
            }

            ChatInputTextBox.Text = promptText;
            await SendCurrentMessageAsync();
        }

        private async void OnSendClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await SendCurrentMessageAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnSendClick: {ex.Message}");
            }
        }

        private async void OnChatInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            try
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    var shiftDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                    if (!shiftDown)
                    {
                        e.Handled = true;
                        await SendCurrentMessageAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnChatInputPreviewKeyDown: {ex.Message}");
            }
        }

        private async Task SendCurrentMessageAsync()
        {
            try
            {
                string text = ChatInputTextBox.Text.Trim();
                if (string.IsNullOrEmpty(text)) return;

                if (string.Equals(text, "/full", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "/fullaccess", StringComparison.OrdinalIgnoreCase))
                {
                    ChatInputTextBox.Text = string.Empty;
                    AgentPresetBox.SelectedIndex = 1;
                    return;
                }

                if (string.Equals(text, "/std", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "/standard", StringComparison.OrdinalIgnoreCase))
                {
                    ChatInputTextBox.Text = string.Empty;
                    AgentPresetBox.SelectedIndex = 0;
                    return;
                }

                ChatInputTextBox.Text = string.Empty;
                if (_typingPushed)
                {
                    _typingPushed = false;
                    _ = _apiService?.PushVtsActivityAsync(false);
                }

                await SendAgentTurnAsync(text, AgentTurnSource.Typed, CancellationToken.None);
            }
            catch (Exception ex)
            {
                FailActiveAgentReply(ex);
                ShowAgentError(ex);
            }
        }

        private async Task SendAgentTurnAsync(
            string text,
            AgentTurnSource source,
            CancellationToken cancellationToken,
            bool allowSessionRecovery = true)
        {
            if (allowSessionRecovery)
            {
                _agentSessionRecovery.Reset();
            }

            if (_agentController is null)
            {
                throw new InvalidOperationException("Agent controller is unavailable.");
            }

            if (IsActiveAgentRunPending())
            {
                ShowAgentInlineStatus("Coding Agent đang có phiên chạy. Hãy hoàn tất phiên hiện tại trước.");
                if (source == AgentTurnSource.Voice)
                {
                    throw new InvalidOperationException("Coding Agent đang xử lý lượt trước.");
                }

                return;
            }

            if (source == AgentTurnSource.Typed && _voiceConversation?.IsRunning == true)
            {
                await StopVoiceConversationAsync();
            }

            await EnsureAgentSessionAsync(cancellationToken);

            string workspace = AgentWorkspaceSelection.Resolve(null, _defaultAgentWorkspace)
                ?? Environment.CurrentDirectory;

            _agentWorkspace = workspace;

            AgentPermissionPreset preset = SelectedAgentPreset();
            if (preset == AgentPermissionPreset.FullAccess && !await ConfirmFullAccessAsync())
            {
                AgentPresetBox.SelectedIndex = 0;
                _agentPreset = AgentPermissionPreset.Standard;
                UpdateAgentInlineState();
                if (source == AgentTurnSource.Voice)
                {
                    throw new OperationCanceledException("Đã hủy lựa chọn Full Access.");
                }

                return;
            }

            _agentPreset = preset;
            try
            {
                long presetVersion = await EnsureAgentPresetAsync(preset, cancellationToken);
                string wirePreset = preset == AgentPermissionPreset.FullAccess ? "full_access" : "standard";
                var request = new CreateAgentRunRequest(
                    _activeSessionId,
                    workspace,
                    text,
                    wirePreset,
                    presetVersion,
                    Guid.NewGuid().ToString("N"));

                ClearAgentActivities();
                ClearAgentRunSummary();
                BeginAgentReply(source, text);
                ScrollChatToBottom();
                ShowAgentInlineStatus("Đang chuẩn bị thao tác…");
                AgentStopButton.Visibility = Visibility.Collapsed;
                UpdateAgentInlineState();
                await _agentController.CreateAndRunAsync(request, cancellationToken);
            }
            catch (AgentApiException error) when (
                allowSessionRecovery
                && AgentSessionRecovery.IsRecoverable(error)
                && _agentSessionRecovery.TryBegin(_activeSessionId))
            {
                // Preserve the voice completion gate while replacing the stale
                // session and retrying this same logical turn once.
                AbandonActiveAgentReply(notifyVoice: false);
                await RecoverAgentSessionAsync(cancellationToken);
                await SendAgentTurnAsync(
                    text,
                    source,
                    cancellationToken,
                    allowSessionRecovery: false);
                return;
            }

            if (IsTerminalStatus(_agentController.CurrentRun?.Status))
            {
                ClearAgentActivities();
                await CompleteActiveAgentTurnAsync(cancellationToken);
            }
            else
            {
                StartAgentMonitor();
            }
            UpdateAgentInlineState();
        }

        private void BeginAgentReply(AgentTurnSource source, string text)
        {
            _activeAgentTurnSource = source;
            _activeAgentRunId = null;
            _activeAgentReplyReceived = false;
            _activeAgentReplyCompleted = false;
            _agentDeltaKeys.Clear();
            _replyAccumulator.Invalidate();

            Messages.Add(new ChatMessageItem
            {
                Role = "user",
                Content = text,
                SessionId = _activeSessionId,
            });
            _activeAgentAssistantMessage = new ChatMessageItem
            {
                Role = "assistant",
                Content = string.Empty,
                IsStreaming = true,
                SessionId = _activeSessionId,
            };
            Messages.Add(_activeAgentAssistantMessage);

            if (source == AgentTurnSource.Voice)
            {
                _harnessEvents?.Write("voice.agent.sent");
            }

            if (_voicePlayback?.AutoReadReplies == true)
            {
                _voicePlayback.BeginReplyStream();
            }
        }

        private async Task CompleteActiveAgentTurnAsync(CancellationToken cancellationToken)
        {
            if (_activeAgentReplyCompleted)
            {
                return;
            }

            _activeAgentReplyCompleted = true;
            // Flush any pending coalesced render before completing
            AgentReplySnapshot? pending = null;
            lock (_renderGate) { pending = _replyAccumulator.TakeSnapshot(); }
            if (pending != null && _activeAgentAssistantMessage != null)
            {
                _activeAgentAssistantMessage.Content = pending.Text;
                _activeAgentAssistantMessage.IsStreaming = false;
                ScrollChatToBottom();
            }
            else if (_activeAgentAssistantMessage != null)
            {
                _activeAgentAssistantMessage.IsStreaming = false;
            }
            _replyAccumulator.Invalidate();
            lock (_renderGate) { _renderPending = false; }
            ChatMessageItem? placeholder = _activeAgentAssistantMessage;

            string? latestAssistant = await ReloadSessionHistoryAsync(cancellationToken);
            string fallbackText = latestAssistant
                ?? StripDisplayMarkers(placeholder?.Content ?? string.Empty);
            if (_activeAgentTurnSource == AgentTurnSource.Voice)
            {
                _harnessEvents?.Write("voice.reply.received");
                if (_voicePlayback?.AutoReadReplies == true)
                {
                    // A streamed reply already entered the voice pipeline through
                    // OnAgentAssistantDeltaReceived. CompleteReplyStreamAsync only
                    // uses fallback text when no typed delta arrived.
                    await _voicePlayback.CompleteReplyStreamAsync(
                        _activeAgentReplyReceived ? string.Empty : fallbackText);
                }

                _harnessEvents?.Write("voice.reply.completed");
                _voiceConversation?.NotifyReplyCompleted();
            }

            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            _agentDeltaKeys.Clear();
            _activeAgentTurnSource = AgentTurnSource.Typed;
        }

        private void FailActiveAgentReply(Exception exception)
        {
            _replyAccumulator.Invalidate();
            lock (_renderGate) { _renderPending = false; }
            if (_activeAgentAssistantMessage is not null)
            {
                _activeAgentAssistantMessage.Content = string.IsNullOrWhiteSpace(
                    _activeAgentAssistantMessage.Content)
                    ? "Coding Agent không thể hoàn thành lượt này. Hãy thử lại sau."
                    : $"{_activeAgentAssistantMessage.Content}\n\nCoding Agent không thể hoàn thành lượt này.";
                _activeAgentAssistantMessage.IsStreaming = false;
                _activeAgentAssistantMessage.IsError = true;
            }

            if (_activeAgentTurnSource == AgentTurnSource.Voice)
            {
                _voicePlayback?.FailReplyStream();
                _voiceConversation?.NotifyReplyError(exception);
            }

            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            _agentDeltaKeys.Clear();
            _activeAgentTurnSource = AgentTurnSource.Typed;
        }

        private void AbandonActiveAgentReply(bool notifyVoice)
        {
            _replyAccumulator.Invalidate();
            lock (_renderGate) { _renderPending = false; }
            _voicePlayback?.StopVoice();
            if (notifyVoice && _activeAgentTurnSource == AgentTurnSource.Voice)
            {
                _voiceConversation?.NotifyReplyError(
                    new OperationCanceledException("Agent turn was replaced or cancelled."));
            }

            if (_activeAgentAssistantMessage is not null)
            {
                _activeAgentAssistantMessage.IsStreaming = false;
            }

            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            _activeAgentReplyReceived = false;
            _activeAgentReplyCompleted = false;
            _agentDeltaKeys.Clear();
            _activeAgentTurnSource = AgentTurnSource.Typed;
        }

        private async Task RecoverAgentSessionAsync(CancellationToken cancellationToken)
        {
            if (_apiService is null)
            {
                throw new InvalidOperationException("Backend session is unavailable.");
            }

            SessionInfo? created = await _apiService.CreateSessionAsync();
            cancellationToken.ThrowIfCancellationRequested();
            string? sessionId = created?.Id?.Trim();
            if (!AgentSessionSelection.IsValid(sessionId))
            {
                throw new AgentApiException(
                    "session_create_failed",
                    "Không tạo được session mới cho Coding Agent.");
            }

            _activeSessionId = sessionId!;
            _agentPresetSessionId = null;
            _agentPresetVersion = null;
            ClearAgentActivities();
            ClearAgentRunSummary();
            Messages.Clear();
            UpdateViewMode();
            SessionOpened?.Invoke(
                this,
                new SessionOpenedEventArgs(
                    sessionId!,
                    created?.Title ?? "Cuộc trò chuyện mới"));
        }

        private async Task EnsureAgentSessionAsync(CancellationToken cancellationToken)
        {
            if (AgentSessionSelection.IsValid(_activeSessionId))
            {
                return;
            }

            if (_apiService is null)
            {
                throw new InvalidOperationException("Backend session is unavailable.");
            }

            SessionInfo? created = await _apiService.CreateSessionAsync();
            cancellationToken.ThrowIfCancellationRequested();
            string? sessionId = created?.Id?.Trim();
            if (!AgentSessionSelection.IsValid(sessionId))
            {
                throw new AgentApiException(
                    "session_create_failed",
                    "Không tạo được session cho Coding Agent.");
            }

            string validSessionId = sessionId!;
            _activeSessionId = validSessionId;
            _agentPresetSessionId = null;
            _agentPresetVersion = null;
            SessionOpened?.Invoke(
                this,
                new SessionOpenedEventArgs(
                    validSessionId,
                    created?.Title ?? "Cuộc trò chuyện mới"));
        }

        private async Task<long> EnsureAgentPresetAsync(
            AgentPermissionPreset preset,
            CancellationToken cancellationToken)
        {
            if (_agentController is null)
            {
                throw new InvalidOperationException("Agent controller is unavailable.");
            }

            if (string.Equals(_agentPresetSessionId, _activeSessionId, StringComparison.Ordinal)
                && _agentPreset == preset
                && _agentPresetVersion is long knownVersion)
            {
                return knownVersion;
            }

            string wirePreset = preset == AgentPermissionPreset.FullAccess ? "full_access" : "standard";
            AgentApiException? lastConflict = null;
            for (long expectedVersion = 0; expectedVersion <= 3; expectedVersion++)
            {
                try
                {
                    long version = await _agentController.SetSessionPresetAsync(
                        _activeSessionId,
                        wirePreset,
                        expectedVersion,
                        preset == AgentPermissionPreset.FullAccess,
                        cancellationToken);
                    _agentPresetSessionId = _activeSessionId;
                    _agentPresetVersion = version;
                    return version;
                }
                catch (AgentApiException ex) when (ex.ErrorCode == "preset_version_conflict")
                {
                    lastConflict = ex;
                }
            }

            if (lastConflict is not null)
            {
                throw lastConflict;
            }

            throw new InvalidOperationException("Không cập nhật được preset quyền cho Agent.");
        }

        private async Task<bool> ConfirmFullAccessAsync()
        {
            var dialog = new ContentDialog
            {
                Title = "Xác nhận Full Access",
                Content = "Full Access chạy dưới quyền người dùng Windows hiện tại và có thể yêu cầu terminal hoặc mạng theo chính sách của run. Đây không phải sandbox; các hành động rủi ro vẫn yêu cầu phê duyệt riêng.",
                PrimaryButtonText = "Tiếp tục",
                CloseButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private static bool IsTerminalStatus(string? status) => status is
            "completed" or "failed" or "cancelled" or "interrupted";

        private void ShowAgentError(Exception exception)
        {
            ClearAgentActivities();
            ClearAgentRunSummary();
            string status = exception switch
            {
                AgentApiException api => api.ErrorCode switch
                {
                    "invalid_workspace" => "Thư mục làm việc không hợp lệ hoặc không tồn tại.",
                    "workspace_busy" => "Thư mục đang được một phiên Agent khác sử dụng.",
                    "preset_mismatch" or "preset_version_conflict" => "Preset quyền đã thay đổi. Hãy chọn lại preset rồi thử lại.",
                    "session_create_failed" => "Không tạo được phiên trò chuyện cho Coding Agent. Kiểm tra backend rồi thử lại.",
                    "session_not_found" or "invalid_session_id" => "Phiên trò chuyện không còn hợp lệ. Hãy gửi lại để Ru tạo phiên mới.",
                    "unauthorized" => "Phiên đăng nhập Agent không còn hợp lệ. Hãy khởi động lại backend.",
                    _ => "Coding Agent không thể bắt đầu. Mở Công cụ → Nhật ký hoạt động để kiểm tra."
                },
                _ => "Coding Agent không thể bắt đầu. Kiểm tra project root và backend rồi thử lại."
            };
            ShowAgentInlineStatus(status);
            AgentStopButton.Visibility = Visibility.Collapsed;
            AgentTrustPanel.Visibility = Visibility.Collapsed;
            AgentApprovalPanel.Visibility = Visibility.Collapsed;
            AgentContinueButton.Visibility = Visibility.Collapsed;
        }

        private void StartAgentMonitor()
        {
            var previous = _agentMonitorCts;
            _agentMonitorCts = new CancellationTokenSource();
            previous?.Cancel();
            previous?.Dispose();
            _ = MonitorAgentRunAsync(_agentMonitorCts.Token);
        }

        private async Task MonitorAgentRunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _agentController is not null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    if (_agentController.CurrentRun is null)
                    {
                        return;
                    }

                    if (IsTerminalStatus(_agentController.CurrentRun.Status))
                    {
                        ClearAgentActivities();
                        await CompleteActiveAgentTurnAsync(cancellationToken);
                        return;
                    }

                    await _agentController.RefreshAsync(cancellationToken);
                    UpdateAgentInlineState();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ShowAgentError(exception);
            }
        }

        private async Task<string?> ReloadSessionHistoryAsync(CancellationToken cancellationToken)
        {
            if (_apiService is null)
            {
                return null;
            }

            var history = await _apiService.GetSessionMessagesAsync(_activeSessionId);
            if (history is null || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            string? latestAssistant = history
                .LastOrDefault(item => string.Equals(item.Role, "assistant", StringComparison.Ordinal))
                .Content;
            latestAssistant = StripDisplayMarkers(latestAssistant ?? string.Empty);

            void Apply()
            {
                ClearAgentActivities();
                Messages.Clear();
                foreach (var (role, content) in history)
                {
                    Messages.Add(new ChatMessageItem
                    {
                        Role = role,
                        Content = role == "assistant" ? StripDisplayMarkers(content) : content
                    });
                }

                if (history.Count > 0)
                {
                    ScrollChatToBottom();
                }

                UpdateViewMode();
            }

            if (DispatcherQueue.HasThreadAccess)
            {
                Apply();
            }
            else
            {
                var applied = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        Apply();
                        applied.TrySetResult(true);
                    }
                    catch (Exception error)
                    {
                        applied.TrySetException(error);
                    }
                }))
                {
                    return null;
                }

                await applied.Task.ConfigureAwait(false);
            }

            return latestAssistant;
        }

        private async Task SendVoiceMessageAsync(string text, CancellationToken cancellationToken)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                await SendVoiceMessageOnUiThreadAsync(text, cancellationToken);
                return;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await SendVoiceMessageOnUiThreadAsync(text, cancellationToken);
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
            {
                throw new InvalidOperationException("Không thể chuyển lượt nói sang UI thread.");
            }

            await completion.Task.ConfigureAwait(false);
        }

        private async Task SendVoiceMessageOnUiThreadAsync(string text, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendAgentTurnAsync(text, AgentTurnSource.Voice, cancellationToken);
            }
            catch (Exception ex)
            {
                FailActiveAgentReply(ex);
                ShowAgentError(ex);
                throw;
            }
        }

        private void ApplyInputWidth()
        {
            if (RootGrid == null || RootGrid.ActualWidth <= 0) return;

            double horizontalInset = RootGrid.ActualWidth < 900
                ? 24
                : RootGrid.ActualWidth < 1280
                    ? 48
                    : 96;
            double availableWidth = Math.Max(160, RootGrid.ActualWidth - horizontalInset);
            double targetWidth = Math.Min(ReadingColumnMaxWidth, availableWidth);

            if (WelcomeHeroPanel != null)
            {
                WelcomeHeroPanel.Width = targetWidth;
            }

            if (InputContainer != null)
            {
                InputContainer.Width = targetWidth;
            }

            if (InputBorder != null)
            {
                InputBorder.Width = targetWidth;
            }

            if (ChatContentGrid != null)
            {
                ChatContentGrid.Width = targetWidth;
            }

            if (ChatListView != null)
            {
                ChatListView.Width = targetWidth;
            }
        }

        private void ScrollChatToBottom()
        {
            ChatScrollHost.UpdateLayout();
            if (ChatScrollHost.ScrollableHeight > 0)
            {
                ChatScrollHost.ChangeView(null, ChatScrollHost.ScrollableHeight, null, disableAnimation: true);
                return;
            }
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RetryScrollAfterLayout);
        }

        private void RetryScrollAfterLayout()
        {
            ChatScrollHost.UpdateLayout();
            if (ChatScrollHost.ScrollableHeight > 0)
            {
                ChatScrollHost.ChangeView(null, ChatScrollHost.ScrollableHeight, null, disableAnimation: true);
            }
        }


        private async void OnAgentStopClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                () => _agentController?.CancelAsync() ?? Task.CompletedTask,
                "Đang dừng Coding Agent…");
            AbandonActiveAgentReply(notifyVoice: true);
            ClearAgentActivities();
            ClearAgentRunSummary();
        }

        private async void OnAgentContinueClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                () => _agentController?.ContinueAsync() ?? Task.CompletedTask,
                "Đang tiếp tục Coding Agent…");
        }

        private async void OnAgentTrustApproveClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                () => _agentController?.TrustAsync(approve: true) ?? Task.CompletedTask,
                "Đang xác nhận workspace…");
        }

        private async void OnAgentTrustDenyClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                () => _agentController?.TrustAsync(approve: false) ?? Task.CompletedTask,
                "Đang từ chối workspace…");
        }

        private async void OnAgentApproveClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                ApprovePendingAsync,
                "Đang gửi phê duyệt…");
        }

        private Task ApprovePendingAsync()
        {
            var scope = (AgentApprovalScopeBox.SelectedItem as ComboBoxItem)?.Tag as string;
            if (scope is "run" or "project")
            {
                return _agentController?.ApproveAsync(scope) ?? Task.CompletedTask;
            }

            // Keep the default one-time path explicit. Besides being the safest
            // default, this preserves the controller's existing API contract for
            // approval cards that do not need a reusable rule.
            return _agentController?.ApproveAsync() ?? Task.CompletedTask;
        }

        private async void OnAgentDenyClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                () => _agentController?.DenyAsync() ?? Task.CompletedTask,
                "Đang từ chối phê duyệt…");
        }

        private async Task RunAgentActionAsync(Func<Task> action, string status)
        {
            if (_agentActionPending || _agentController is null)
            {
                return;
            }

            _agentActionPending = true;
            ShowAgentInlineStatus(status);
            UpdateAgentInlineState();
            try
            {
                await action();
                StartAgentMonitor();
            }
            catch (Exception exception)
            {
                ShowAgentError(exception);
            }
            finally
            {
                _agentActionPending = false;
                UpdateAgentInlineState();
            }
        }

        public async Task OpenSessionAsync(string sessionId, string title)
        {
            try
            {
                if (_apiService == null) return;
                await ReplaceVoiceSessionAsync();
                _activeSessionId = sessionId;
                ResetAgentState();
                SessionOpened?.Invoke(this, new SessionOpenedEventArgs(sessionId, title));
                Messages.Clear();
                var history = await _apiService.GetSessionMessagesAsync(_activeSessionId);
                if (history != null)
                {
                    foreach (var (role, content) in history)
                    {
                        Messages.Add(new ChatMessageItem
                        {
                            Role = role,
                            Content = role == "assistant" ? StripDisplayMarkers(content) : content
                        });
                    }
                    
                    if (history.Count > 0)
                    {
                        ScrollChatToBottom();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OpenSessionAsync: {ex.Message}");
                Messages.Add(new ChatMessageItem
                {
                    Role = "assistant",
                    Content = "**LỖI MỞ SESSION:** Không thể tải cuộc trò chuyện. Hãy kiểm tra backend rồi thử lại.",
                    IsError = true
                });
            }
        }

        private async void OnMicClick(object sender, RoutedEventArgs e)
        {
            if (_voiceConversation == null)
            {
                return;
            }

            try
            {
                if (_voiceConversation.IsRunning)
                {
                    await StopVoiceConversationAsync();
                    return;
                }

                _voicePlayback?.StopVoice();
                await _voiceConversation.StartAsync();
                SetVoiceButtonState(true);
            }
            catch (Exception ex)
            {
                SetVoiceButtonState(false);
                ChatInputTextBox.PlaceholderText = "Không truy cập được micro. Kiểm tra quyền mic.";
                System.Diagnostics.Debug.WriteLine($"Error starting voice conversation: {ex.Message}");
            }
        }

        private async Task StopVoiceConversationAsync()
        {
            if (_voiceConversation == null || !_voiceConversation.IsRunning)
            {
                _voicePlayback?.StopVoice();
                SetVoiceButtonState(false);
                return;
            }

            await _voiceConversation.StopAsync();
            AbandonActiveAgentReply(notifyVoice: false);
            _harnessEvents?.Write(
                string.Equals(_harnessScenario, "cancel", StringComparison.OrdinalIgnoreCase)
                    ? "voice.conversation.cancelled"
                    : "voice.conversation.stopped");
            _voicePlayback?.StopVoice();
            SetVoiceButtonState(false);
        }

        private async Task ReplaceVoiceSessionAsync()
        {
            if (_voiceConversation != null)
            {
                await _voiceConversation.ReplaceSessionAsync();
            }

            AbandonActiveAgentReply(notifyVoice: false);
            SetVoiceButtonState(false);
        }

        private async Task<string?> TranscribeVoiceAsync(byte[] wavBytes, CancellationToken cancellationToken)
        {
            if (_apiService == null)
            {
                throw new InvalidOperationException("Voice API chưa sẵn sàng.");
            }

            string? text = await _apiService.TranscribeAudioAsync(wavBytes, cancellationToken);
            if (text == null)
            {
                throw new InvalidOperationException("Dịch vụ nhận dạng giọng nói không khả dụng.");
            }

            _harnessEvents?.Write("voice.transcribe.completed");
            return text;
        }

        private void OnVoiceConversationStatusChanged(object? sender, VoiceConversationStatus status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (status.State == VoiceConversationState.Stopped)
                {
                    SetVoiceButtonState(false);
                    VoiceConversationStatusText.Text = "Đã dừng";
                    VoiceConversationStatusText.Visibility = Visibility.Visible;
                    return;
                }

                if (status.State == VoiceConversationState.Error)
                {
                    SetVoiceButtonState(false);
                    ChatInputTextBox.PlaceholderText = status.Message
                        ?? "Cuộc trò chuyện bằng giọng nói đã dừng.";
                    VoiceConversationStatusText.Text = status.Message ?? "Không truy cập được micro.";
                    VoiceConversationStatusText.Visibility = Visibility.Visible;
                    return;
                }

                SetVoiceButtonState(true);
                VoiceConversationStatusText.Text = status.State switch
                {
                    VoiceConversationState.Listening => "Đang nghe...",
                    VoiceConversationState.Transcribing => "Đang xử lý...",
                    VoiceConversationState.Sending or VoiceConversationState.WaitingForReply => "Đang chờ Ru trả lời...",
                    _ => status.Message ?? string.Empty,
                };
                VoiceConversationStatusText.Visibility = Visibility.Visible;
                if (status.State == VoiceConversationState.Listening)
                {
                    _harnessEvents?.Write("voice.capture.started");
                }
                else if (status.State == VoiceConversationState.Transcribing)
                {
                    _harnessEvents?.Write("voice.capture.ended");
                }
                if (!string.IsNullOrWhiteSpace(status.Message))
                {
                    ChatInputTextBox.PlaceholderText = status.Message;
                }
            });
        }

        private void SetVoiceButtonState(bool active)
        {
            MicButton.IsEnabled = true;
            MicIcon.Glyph = active ? "\uE768" : "\uE720";
            MicIcon.FontSize = 14;
            MicIcon.Foreground = active
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : (Brush)Application.Current.Resources["RuMutedBrush"];
            MicButton.Background = active
                ? (SolidColorBrush)Application.Current.Resources["AppDangerBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ToolTipService.SetToolTip(
                MicButton,
                active ? "Dừng trò chuyện bằng giọng nói" : "Nói chuyện với Ru bằng giọng nói");
            AutomationProperties.SetName(
                MicButton,
                active ? "Dừng trò chuyện bằng giọng nói" : "Bắt đầu trò chuyện bằng giọng nói");
        }

        public void Dispose()
        {
            _agentMonitorCts?.Cancel();
            _agentMonitorCts?.Dispose();
            _agentMonitorCts = null;
            if (_agentController is not null)
            {
                _agentController.PropertyChanged -= OnAgentControllerChanged;
                _agentController.ActivityReceived -= OnAgentActivityReceived;
                _agentController.SummaryReceived -= OnAgentSummaryReceived;
                _agentController.AssistantDeltaReceived -= OnAgentAssistantDeltaReceived;
            }
            ClearAgentActivities();
            ClearAgentRunSummary();
            _typingDebounce?.Stop();
            _typingDebounce = null;
            MeteorShowerField?.Dispose();
            _voicePlayback?.StopVoice();
            if (_voiceConversation != null)
            {
                _voiceConversation.StatusChanged -= OnVoiceConversationStatusChanged;
                _voiceConversation.Dispose();
                _voiceConversation = null;
            }
            _voiceCapture?.Dispose();
            _voiceCapture = null;
        }
    }
}
