using System;
using System.Collections.ObjectModel;
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
        public ObservableCollection<ChatMessageItem> Messages { get; } = new();
        private DispatcherTimer? _typingDebounce;
        private bool _typingPushed;
        private bool _voiceTurnPending;
        private readonly AgentSessionService _agentSession = new();
        private AgentApprovalModel? _pendingAgentApproval;
        private bool _approvalResponsePending;
        private readonly DispatcherTimer _approvalExpiryTimer;

        public ChatView()
        {
            this.InitializeComponent();
            ChatListView.ItemsSource = Messages;
            Messages.CollectionChanged += (s, e) => UpdateViewMode();
            RootGrid.SizeChanged += OnRootGridSizeChanged;
            this.Loaded += (s, e) => ApplyInputWidth();
            _approvalExpiryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _approvalExpiryTimer.Tick += (_, __) =>
            {
                if (_pendingAgentApproval?.IsExpired() == true)
                {
                    ClearAgentApproval();
                    AgentActivity.Text = "Yêu cầu duyệt đã hết hạn.";
                    AgentActivity.Visibility = Visibility.Visible;
                }
            };
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
            string? harnessScenario = null)
        {
            _apiService = apiService;
            _wsClient = wsClient;
            _voicePlayback = voicePlayback;
            _harnessEvents = harnessEvents;
            _harnessScenario = harnessScenario;
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
            _wsClient.OnConnected += OnWsConnected;
            _wsClient.OnDisconnected += OnWsDisconnected;
            _wsClient.OnAgentActivityReceived += OnAgentActivityReceived;
            _wsClient.OnAgentApprovalRequired += OnAgentApprovalRequired;
            _wsClient.OnAgentApprovalResult += OnAgentApprovalResult;

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

        private void OnWsConnected()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
            });
        }

        private void OnWsDisconnected()
        {
            DispatcherQueue.TryEnqueue(() => ClearAgentApproval());
        }

        private void OnWsSessionReceived(string sessionId)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _activeSessionId = sessionId;
                _agentSession.Disable();
                AgentModeButton.IsChecked = false;
                AgentActivity.Visibility = Visibility.Collapsed;
                ClearAgentApproval();
            });
        }

        private void OnAgentModeClick(object sender, RoutedEventArgs e)
        {
            if (AgentModeButton.IsChecked == true)
            {
                _agentSession.Enable(_activeSessionId);
                AgentActivity.Text = "Agent session enabled for bounded workspace and terminal tools.";
                AgentActivity.Visibility = Visibility.Visible;
            }
            else
            {
                _agentSession.Disable();
                AgentActivity.Visibility = Visibility.Collapsed;
                ClearAgentApproval();
            }
        }

        private void OnAgentActivityReceived(string content, string sessionId)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.IsNullOrWhiteSpace(sessionId) && sessionId != _activeSessionId)
                {
                    return;
                }

                AgentActivity.Text = content.Length > 256 ? content[..256] : content;
                AgentActivity.Visibility = Visibility.Visible;
                _harnessEvents?.Write("agent.activity", new { state = "running", tool_id = "registered" });
            });
        }

        private void OnAgentApprovalRequired(AgentApprovalModel approval)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (approval.SessionId != _activeSessionId || approval.IsExpired())
                {
                    return;
                }

                _pendingAgentApproval = approval;
                _approvalResponsePending = false;
                AgentApprovalActionText.Text = approval.SafeAction;
                AgentApprovalScopeText.Text = $"Phạm vi: {approval.Scope}";
                AgentApprovalExpiryText.Text = $"Hết hạn lúc {approval.ExpiresAt.ToLocalTime():HH:mm}";
                AgentApprovalCard.Visibility = Visibility.Visible;
                SetApprovalButtonsState(true);
                _approvalExpiryTimer.Start();
                AgentActivity.Text = "Đang chờ anh xác nhận hành động của Agent.";
                AgentActivity.Visibility = Visibility.Visible;
                _harnessEvents?.Write("agent.approval.required", new { state = "required" });
            });
        }

        private void OnAgentApprovalResult(AgentApprovalResult result)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (result.SessionId != _activeSessionId
                    || _pendingAgentApproval?.ApprovalId != result.ApprovalId)
                {
                    return;
                }

                ClearAgentApproval();
                AgentActivity.Text = result.SafeSummary.Length > 256
                    ? result.SafeSummary[..256]
                    : result.SafeSummary;
                AgentActivity.Visibility = Visibility.Visible;
                _harnessEvents?.Write("agent.approval.result", new { state = result.Status });
            });
        }

        private async void OnAgentApproveClick(object sender, RoutedEventArgs e)
        {
            await SendAgentApprovalDecisionAsync(approved: true);
        }

        private async void OnAgentRejectClick(object sender, RoutedEventArgs e)
        {
            await SendAgentApprovalDecisionAsync(approved: false);
        }

        private async Task SendAgentApprovalDecisionAsync(bool approved)
        {
            var approval = _pendingAgentApproval;
            if (approval == null || _approvalResponsePending)
            {
                return;
            }

            if (approval.SessionId != _activeSessionId || approval.IsExpired())
            {
                ClearAgentApproval();
                AgentActivity.Text = "Yêu cầu duyệt đã hết hạn.";
                AgentActivity.Visibility = Visibility.Visible;
                return;
            }

            if (_wsClient == null)
            {
                AgentActivity.Text = "Chưa kết nối được với backend.";
                AgentActivity.Visibility = Visibility.Visible;
                return;
            }

            _approvalResponsePending = true;
            SetApprovalButtonsState(false);
            AgentActivity.Text = "Đang gửi quyết định của anh...";
            AgentActivity.Visibility = Visibility.Visible;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _wsClient.SendAgentApprovalAsync(
                    approval.SessionId,
                    approval.ApprovalId,
                    approved,
                    timeout.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error sending Agent approval: {ex.Message}");
                _approvalResponsePending = false;
                if (_pendingAgentApproval?.ApprovalId == approval.ApprovalId)
                {
                    SetApprovalButtonsState(true);
                    AgentActivity.Text = "Không gửi được quyết định. Anh có thể thử lại.";
                    AgentActivity.Visibility = Visibility.Visible;
                }
            }
        }

        private void SetApprovalButtonsState(bool enabled)
        {
            AgentApproveButton.IsEnabled = enabled;
            AgentRejectButton.IsEnabled = enabled;
        }

        private void ClearAgentApproval()
        {
            _pendingAgentApproval = null;
            _approvalResponsePending = false;
            _approvalExpiryTimer.Stop();
            AgentApprovalCard.Visibility = Visibility.Collapsed;
            SetApprovalButtonsState(true);
            AgentApprovalActionText.Text = string.Empty;
            AgentApprovalScopeText.Text = string.Empty;
            AgentApprovalExpiryText.Text = string.Empty;
        }

        private void OnWsResetReceived()
        {
            if (_voicePlayback?.AutoReadReplies == true)
            {
                _voicePlayback.ResetReplyStreamForRetry();
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                ClearAgentApproval();
                var last = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming);
                if (last != null)
                {
                    Messages.Remove(last);
                }
            });
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
                if (string.IsNullOrEmpty(text) || _wsClient == null) return;

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
                HandleSendError(ex, "SendCurrentMessageAsync");
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
            await _wsClient.SendMessageAsync(text, _activeSessionId, cancellationToken, _agentSession.IsEnabled);
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

            if (ReadingColumn != null)
            {
                ReadingColumn.Width = targetWidth;
            }

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

        public async Task OpenSessionAsync(string sessionId, string title)
        {
            try
            {
                if (_apiService == null) return;
                await ReplaceVoiceSessionAsync();
                _activeSessionId = sessionId;
                ClearAgentApproval();
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
            MicIcon.Glyph = active ? "\uE7C7" : "\uE720";
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
                _wsClient.OnAgentActivityReceived -= OnAgentActivityReceived;
                _wsClient.OnAgentApprovalRequired -= OnAgentApprovalRequired;
                _wsClient.OnAgentApprovalResult -= OnAgentApprovalResult;
                _wsClient.OnConnected -= OnWsConnected;
            }
            _typingDebounce?.Stop();
            _typingDebounce = null;
            _approvalExpiryTimer.Stop();
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
