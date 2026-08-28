using System;
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
        private ChatWebSocketClient? _wsClient;
        private VoicePlaybackService? _voicePlayback;
        private VoiceTurnCaptureService? _voiceCapture;
        private VoiceConversationService? _voiceConversation;
        private HarnessEventSink? _harnessEvents;
        private string? _harnessScenario;
        private string _activeSessionId = "default";
        private AgentRunController? _agentController;
        private CancellationTokenSource? _agentMonitorCts;
        private bool _agentMode;
        private bool _agentActivityVisible;
        private AgentPermissionPreset _agentPreset = AgentPermissionPreset.Standard;
        private string? _agentPresetSessionId;
        private long? _agentPresetVersion;
        private string _agentWorkspace = Environment.CurrentDirectory;
        private string? _defaultAgentWorkspace;
        private bool _agentActionPending;
        public ObservableCollection<ChatMessageItem> Messages { get; } = new();
        private DispatcherTimer? _typingDebounce;
        private bool _typingPushed;
        private bool _voiceTurnPending;

        public ChatView()
        {
            this.InitializeComponent();
            ChatListView.ItemsSource = Messages;
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
            ChatWebSocketClient wsClient,
            VoicePlaybackService voicePlayback,
            Func<IVoiceFrameSource>? voiceSourceFactory = null,
            HarnessEventSink? harnessEvents = null,
            string? harnessScenario = null,
            AgentRunController? agentController = null,
            string? defaultAgentWorkspace = null)
        {
            _apiService = apiService;
            _wsClient = wsClient;
            _voicePlayback = voicePlayback;
            _harnessEvents = harnessEvents;
            _harnessScenario = harnessScenario;
            _agentController = agentController;
            string defaultWorkspace = string.IsNullOrWhiteSpace(defaultAgentWorkspace)
                ? Environment.CurrentDirectory
                : defaultAgentWorkspace.Trim();
            _defaultAgentWorkspace = defaultWorkspace;
            _agentWorkspace = defaultWorkspace;
            AgentWorkspaceBox.Text = defaultWorkspace;
            _agentController?.PropertyChanged += OnAgentControllerChanged;
            UpdateAgentModeVisuals();
            UpdateAgentInlineState();
            _voiceCapture = new VoiceTurnCaptureService(voiceSourceFactory);
            _voiceConversation = new VoiceConversationService(
                _voiceCapture,
                TranscribeVoiceAsync,
                SendVoiceMessageAsync,
                harnessEvents);
            _voiceConversation.StatusChanged += OnVoiceConversationStatusChanged;

            _wsClient.OnDeltaReceived += OnWsDeltaReceived;
            _wsClient.OnDoneReceived += OnWsDoneReceived;
            _wsClient.OnErrorReceived += OnWsErrorReceived;
            _wsClient.OnSessionReceived += OnWsSessionReceived;
            _wsClient.OnResetReceived += OnWsResetReceived;
            _wsClient.OnDisconnected += OnWsDisconnected;

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

        private void OnAgentModeButtonClick(object sender, RoutedEventArgs e)
        {
            UpdateAgentModeVisuals();
        }

        private void OnAgentModeOptionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton option || option.Tag is not string tag)
            {
                return;
            }

            SetAgentMode(string.Equals(tag, "agent", StringComparison.Ordinal));
        }

        private void OnAgentPresetChanged(object sender, SelectionChangedEventArgs e)
        {
            _agentPreset = SelectedAgentPreset();
            UpdateAgentInlineState();
        }

        private void SetAgentMode(bool enabled)
        {
            if (!enabled
                && IsActiveAgentRunPending())
            {
                _agentMode = true;
                AgentModeAgentOption.IsChecked = true;
                ShowAgentInlineStatus("Coding Agent đang có phiên chạy. Hãy Dừng hoặc hoàn tất phiên trước.");
                UpdateAgentModeVisuals();
                UpdateAgentInlineState();
                return;
            }

            _agentMode = enabled;
            _agentActivityVisible = enabled && IsActiveAgentRunPending();
            if (!enabled)
            {
                _agentActivityVisible = false;
                _agentPreset = AgentPermissionPreset.Standard;
                _agentPresetSessionId = null;
                _agentPresetVersion = null;
                AgentPresetBox.SelectedIndex = 0;
                AgentInlinePanel.Visibility = Visibility.Collapsed;
            }
            else if (!_agentActivityVisible)
            {
                AgentStatusText.Text = string.Empty;
                AgentInlinePanel.Visibility = Visibility.Collapsed;
            }

            UpdateAgentModeVisuals();
            UpdateAgentInlineState();
        }

        private void ShowAgentInlineStatus(string status)
        {
            _agentActivityVisible = true;
            AgentInlinePanel.Visibility = Visibility.Visible;
            AgentStatusText.Text = status;
        }

        private void UpdateAgentModeVisuals()
        {
            if (AgentModeChatOption is null)
            {
                return;
            }

            AgentModeChatOption.IsChecked = !_agentMode;
            AgentModeAgentOption.IsChecked = _agentMode;
            AgentModeLabel.Text = _agentMode ? "Agent" : "Chat";
            AgentModeIcon.Glyph = _agentMode ? "\uEC25" : "\uE8F2";
            AgentSettingsButton.Visibility = _agentMode ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(
                AgentModeButton,
                _agentMode ? "Đang ở chế độ Coding Agent" : "Đang ở chế độ Trò chuyện");
            ToolTipService.SetToolTip(
                AgentModeButton,
                _agentMode ? "Coding Agent · mở để đổi chế độ" : "Chat · mở để bật Coding Agent");
            MicButton.IsEnabled = !_agentMode;
            AutomationProperties.SetName(
                MicButton,
                _agentMode
                    ? "Micro bị tắt trong Coding Agent; hãy nhập chỉ thị bằng bàn phím"
                    : "Bắt đầu trò chuyện bằng giọng nói");
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


        private void OnWsSessionReceived(string sessionId)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return;
                }

                _activeSessionId = sessionId;
                ResetAgentState();
            });
        }

        private void OnWsDisconnected()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ResetAgentState();
                ShowAgentInlineStatus("Mất kết nối backend. Coding Agent đã tạm dừng giao diện.");
            });
        }


        private void OnWsResetReceived()
        {
            if (_voicePlayback?.AutoReadReplies == true)
            {
                _voicePlayback.ResetReplyStreamForRetry();
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                ResetAgentState();
                var last = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming);
                if (last != null)
                {
                    Messages.Remove(last);
                }
            });
        }

        private void ResetAgentState()
        {
            if (_agentController?.CanCancel == true)
            {
                _ = _agentController.CancelAsync();
            }

            var monitor = _agentMonitorCts;
            _agentMonitorCts = null;
            monitor?.Cancel();
            monitor?.Dispose();
            _agentMode = false;
            _agentActivityVisible = false;
            _agentPreset = AgentPermissionPreset.Standard;
            _agentPresetSessionId = null;
            _agentPresetVersion = null;
            _agentActionPending = false;
            if (AgentPresetBox is not null)
            {
                AgentPresetBox.SelectedIndex = 0;
            }

            UpdateAgentModeVisuals();
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

                if (_agentMode)
                {
                    if (_agentController is null)
                    {
                        ShowAgentInlineStatus("Coding Agent chưa sẵn sàng.");
                        return;
                    }

                    ChatInputTextBox.Text = string.Empty;
                    if (_typingPushed)
                    {
                        _typingPushed = false;
                        _ = _apiService?.PushVtsActivityAsync(false);
                    }

                    await SendAgentMessageAsync(text, CancellationToken.None);
                    return;
                }

                if (_wsClient == null) return;

                await StopVoiceConversationAsync();

                ChatInputTextBox.Text = string.Empty;
                if (_typingPushed)
                {
                    _typingPushed = false;
                    _ = _apiService?.PushVtsActivityAsync(false);
                }

                await SendMessageTextAsync(text, CancellationToken.None);
            }
            catch (Exception ex)
            {
                if (_agentMode)
                {
                    ShowAgentError(ex);
                }
                else
                {
                    HandleSendError(ex, "SendCurrentMessageAsync");
                }
            }
        }

        private async Task SendAgentMessageAsync(string text, CancellationToken cancellationToken)
        {
            if (_agentController is null)
            {
                throw new InvalidOperationException("Agent controller is unavailable.");
            }

            if (IsActiveAgentRunPending())
            {
                ShowAgentInlineStatus("Coding Agent đang có phiên chạy. Hãy hoàn tất phiên hiện tại trước.");
                return;
            }

            string? workspace = AgentWorkspaceSelection.Resolve(
                AgentWorkspaceBox.Text,
                _defaultAgentWorkspace);
            if (workspace is null)
            {
                AgentSettingsFlyout.ShowAt(AgentSettingsButton);
                AgentWorkspaceBox.Focus(FocusState.Programmatic);
                ShowAgentInlineStatus("Chưa xác định được thư mục làm việc cho Coding Agent.");
                return;
            }

            _agentWorkspace = workspace;

            AgentPermissionPreset preset = SelectedAgentPreset();
            if (preset == AgentPermissionPreset.FullAccess && !await ConfirmFullAccessAsync())
            {
                AgentPresetBox.SelectedIndex = 0;
                _agentPreset = AgentPermissionPreset.Standard;
                UpdateAgentInlineState();
                return;
            }

            _agentPreset = preset;
            long presetVersion = await EnsureAgentPresetAsync(preset, cancellationToken);
            string wirePreset = preset == AgentPermissionPreset.FullAccess ? "full_access" : "standard";
            var request = new CreateAgentRunRequest(
                _activeSessionId,
                workspace,
                text,
                wirePreset,
                presetVersion,
                Guid.NewGuid().ToString("N"));

            ShowAgentInlineStatus("Đang chuẩn bị thao tác…");
            AgentStopButton.Visibility = Visibility.Collapsed;
            UpdateAgentInlineState();
            await _agentController.CreateAndRunAsync(request, cancellationToken);

            if (IsTerminalStatus(_agentController.CurrentRun?.Status))
            {
                await ReloadSessionHistoryAsync(cancellationToken);
            }
            else
            {
                StartAgentMonitor();
            }
            UpdateAgentInlineState();
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
            string status = exception switch
            {
                AgentApiException api => api.ErrorCode switch
                {
                    "invalid_workspace" => "Thư mục làm việc không hợp lệ hoặc không tồn tại.",
                    "workspace_busy" => "Thư mục đang được một phiên Agent khác sử dụng.",
                    "preset_mismatch" or "preset_version_conflict" => "Preset quyền đã thay đổi. Hãy chọn lại preset rồi thử lại.",
                    "unauthorized" => "Phiên đăng nhập Agent không còn hợp lệ. Hãy khởi động lại backend.",
                    _ => "Coding Agent không thể bắt đầu. Mở Công cụ → Nhật ký hoạt động để kiểm tra."
                },
                _ => "Coding Agent không thể bắt đầu. Kiểm tra workspace và backend rồi thử lại."
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
                        await ReloadSessionHistoryAsync(cancellationToken);
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

        private async Task ReloadSessionHistoryAsync(CancellationToken cancellationToken)
        {
            if (_apiService is null)
            {
                return;
            }

            var history = await _apiService.GetSessionMessagesAsync(_activeSessionId);
            if (history is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            void Apply()
            {
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
                DispatcherQueue.TryEnqueue(Apply);
            }
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
                _voiceTurnPending = true;
                await SendMessageTextAsync(text, cancellationToken);
            }
            catch (Exception ex)
            {
                _voiceTurnPending = false;
                HandleSendError(ex, "SendVoiceMessageAsync");
                throw;
            }
        }

        private async Task SendMessageTextAsync(string text, CancellationToken cancellationToken)
        {
            if (_wsClient == null)
            {
                throw new InvalidOperationException("Chat WebSocket chưa sẵn sàng.");
            }

            _voicePlayback?.StopVoice();
            Messages.Add(new ChatMessageItem { Role = "user", Content = text });
            Messages.Add(new ChatMessageItem { Role = "assistant", Content = "", IsStreaming = true });
            if (_voicePlayback?.AutoReadReplies == true)
            {
                _voicePlayback.BeginReplyStream();
            }

            if (_voiceTurnPending)
            {
                _harnessEvents?.Write("voice.chat.sent");
            }

            ScrollChatToBottom();
            cancellationToken.ThrowIfCancellationRequested();
            await _wsClient.SendMessageAsync(text, _activeSessionId, cancellationToken);
        }

        private void HandleSendError(Exception exception, string operation)
        {
            System.Diagnostics.Debug.WriteLine($"Error in {operation}: {exception.Message}");
            _voicePlayback?.StopVoice();
            var last = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming);
            if (last != null)
            {
                last.Content = $"**LỖI GỬI:** {exception.Message}";
                last.IsStreaming = false;
                last.IsError = true;
            }
            else
            {
                Messages.Add(new ChatMessageItem
                {
                    Role = "assistant",
                    Content = $"**LỖI GỬI:** {exception.Message}",
                    IsStreaming = false,
                    IsError = true
                });
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

        private void OnWsDeltaReceived(string delta)
        {
            try
            {
                if (_voicePlayback?.AutoReadReplies == true)
                {
                    _voicePlayback.AppendReplyDelta(delta);
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var last = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming);
                        if (last != null)
                        {
                            last.Content += delta;
                            ScrollChatToBottom();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error in OnWsDeltaReceived queue: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnWsDeltaReceived: {ex.Message}");
            }
        }

        private void OnWsDoneReceived(string content, string sessionId)
        {
            try
            {
                if (_voiceTurnPending)
                {
                    _harnessEvents?.Write("voice.reply.received");
                }

                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        var last = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming);
                        string finalVoiceText = StripDisplayMarkers(
                            string.IsNullOrWhiteSpace(content) ? last?.Content ?? "" : content);
                        if (last != null)
                        {
                            string display = string.IsNullOrWhiteSpace(content)
                                ? StripDisplayMarkers(last.Content)
                                : StripDisplayMarkers(content);
                            last.Content = display;
                            last.IsStreaming = false;
                        }
                        else if (!string.IsNullOrWhiteSpace(content))
                        {
                            Messages.Add(new ChatMessageItem
                            {
                                Role = "assistant",
                                Content = StripDisplayMarkers(content),
                                IsStreaming = false
                            });
                        }

                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            _activeSessionId = sessionId;
                        }

                        if (_voicePlayback != null && _voicePlayback.AutoReadReplies)
                        {
                            await _voicePlayback.CompleteReplyStreamAsync(finalVoiceText);
                        }

                        if (_voiceTurnPending)
                        {
                            _harnessEvents?.Write("voice.reply.completed");
                            _voiceTurnPending = false;
                        }
                        _voiceConversation?.NotifyReplyCompleted();
                    }
                    catch (Exception ex)
                    {
                        if (_voiceTurnPending)
                        {
                            _harnessEvents?.Write("voice.reply.failed");
                        }
                        _voiceConversation?.NotifyReplyError(ex);
                        System.Diagnostics.Debug.WriteLine($"Error in OnWsDoneReceived queue: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnWsDoneReceived: {ex.Message}");
            }
        }

        private void OnWsErrorReceived(string error)
        {
            try
            {
                _voicePlayback?.FailReplyStream();
                _voiceTurnPending = false;
                _voiceConversation?.NotifyReplyError(new InvalidOperationException(error));
                DispatcherQueue.TryEnqueue(() =>
                {
                    var last = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming);
                    if (last == null)
                    {
                        Messages.Add(new ChatMessageItem
                        {
                            Role = "assistant",
                            Content = $"**LỖI:** {error}\n\n{GetErrorDetails(error)}",
                            IsStreaming = false,
                            IsError = true
                        });
                        return;
                    }

                    try
                    {
                        string detailedError = $"**LỖI: {error}**\n\n**Gợi ý sửa:** {GetErrorDetails(error)}";
                        last.Content = string.IsNullOrEmpty(last.Content)
                            ? detailedError
                            : $"{last.Content}\n\n{detailedError}";
                        last.IsStreaming = false;
                        last.IsError = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error updating message: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnWsErrorReceived: {ex.Message}");
            }
        }

        private string GetErrorDetails(string error)
        {
            try
            {
                if (string.IsNullOrEmpty(error))
                {
                    return "Error message is empty";
                }

                if (error.Contains("timeout") || error.Contains("time out"))
                {
                    return "Backend phản hồi quá chậm. Kiểm tra: Backend đang chạy? Model có quá nặng? GPU có đủ bộ nhớ?";
                }
                else if (error.Contains("connection") || error.Contains("connect"))
                {
                    return "Không thể kết nối đến backend. Kiểm tra: Server có chạy (127.0.0.1:8000)? Token có đúng?";
                }
                else if (error.Contains("model") || error.Contains("load"))
                {
                    return "Lỗi kết nối provider. Kiểm tra model name, endpoint và credential trong Settings.";
                }
                else if (error.Contains("memory") || error.Contains("GPU"))
                {
                    return "Lỗi bộ nhớ GPU. Kiểm tra: GPU có đủ VRAM? Có process khác đang dùng GPU?";
                }
                else if (error.Contains("API") || error.Contains("key"))
                {
                    return "Lỗi API key. Kiểm tra: API key có đúng? Có quota còn lại? Network có ổn định?";
                }
                else
                {
                    return "Vui lòng kiểm tra nhật ký trong tab Nhật ký hoặc file log hệ thống.";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in GetErrorDetails: {ex.Message}");
                return "Error details unavailable";
            }
        }

        private async void OnAgentStopClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                () => _agentController?.CancelAsync() ?? Task.CompletedTask,
                "Đang dừng Coding Agent…");
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
                    Content = $"**LỖI MỞ SESSION:** {ex.Message}",
                    IsError = true
                });
            }
        }

        private async void OnMicClick(object sender, RoutedEventArgs e)
        {
            if (_agentMode)
            {
                ShowAgentInlineStatus("Coding Agent nhận chỉ thị bằng bàn phím; micro đang tắt.");
                return;
            }

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
            _voiceTurnPending = false;
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

            _voiceTurnPending = false;
            _voicePlayback?.StopVoice();
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
            if (_wsClient != null)
            {
                _wsClient.OnDeltaReceived -= OnWsDeltaReceived;
                _wsClient.OnDoneReceived -= OnWsDoneReceived;
                _wsClient.OnErrorReceived -= OnWsErrorReceived;
                _wsClient.OnSessionReceived -= OnWsSessionReceived;
                _wsClient.OnResetReceived -= OnWsResetReceived;
                _wsClient.OnDisconnected -= OnWsDisconnected;
            }
            _agentMonitorCts?.Cancel();
            _agentMonitorCts?.Dispose();
            _agentMonitorCts = null;
            if (_agentController is not null)
            {
                _agentController.PropertyChanged -= OnAgentControllerChanged;
            }
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
