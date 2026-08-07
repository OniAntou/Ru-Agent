using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
        public DataTemplate? StreamingTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is ChatMessageItem msg)
            {
                if (msg.Role == "user") return UserTemplate;
                if (msg.IsStreaming) return StreamingTemplate ?? AssistantTemplate;
                return AssistantTemplate;
            }
            return base.SelectTemplateCore(item, container);
        }
    }

    public sealed partial class ChatView : UserControl
    {
        private const string VtsMarkerPattern = @"\s*\[\[(?:VTS|VOICE_PACE):[^\]]*\]\]";
        private const double InputMaxWidth = 860;

        private ApiService? _apiService;
        private ChatWebSocketClient? _wsClient;
        private VoicePlaybackService? _voicePlayback;
        private MicRecorderService _micRecorder = new();
        private string _activeSessionId = "default";
        public ObservableCollection<ChatMessageItem> Messages { get; } = new();
        private bool _isFirstMessage = true;
        private bool _inputCentered = true;
        private double _centerDelta;
        private DispatcherTimer? _typingDebounce;
        private bool _typingPushed;

        public ChatView()
        {
            this.InitializeComponent();
            ChatListView.ItemsSource = Messages;
            RootGrid.SizeChanged += OnRootGridSizeChanged;
            CenterInput();
        }

        private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Luôn đồng bộ chiều rộng vùng chat và khung nhập khi cửa sổ đổi kích thước.
            ApplyInputWidth();

            // Khi chưa có tin nhắn, tiếp tục giữ khung nhập ở giữa theo chiều dọc.
            if (_inputCentered)
            {
                UpdateCenterDelta();
            }
        }

        public event EventHandler<SessionOpenedEventArgs>? SessionOpened;

        public void Initialize(ApiService apiService, ChatWebSocketClient wsClient, VoicePlaybackService voicePlayback)
        {
            _apiService = apiService;
            _wsClient = wsClient;
            _voicePlayback = voicePlayback;

            _wsClient.OnDeltaReceived += OnWsDeltaReceived;
            _wsClient.OnDoneReceived += OnWsDoneReceived;
            _wsClient.OnErrorReceived += OnWsErrorReceived;
            _wsClient.OnSessionReceived += OnWsSessionReceived;
            _wsClient.OnResetReceived += OnWsResetReceived;
            _wsClient.OnConnected += OnWsConnected;

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

        private void OnWsSessionReceived(string sessionId)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _activeSessionId = sessionId;
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

        private async void OnChatInputKeyDown(object sender, KeyRoutedEventArgs e)
        {
            try
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    await SendCurrentMessageAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnChatInputKeyDown: {ex.Message}");
            }
        }

        private async Task SendCurrentMessageAsync()
        {
            try
            {
                string text = ChatInputTextBox.Text.Trim();
                if (string.IsNullOrEmpty(text) || _wsClient == null) return;

                _voicePlayback?.StopVoice();

                ChatInputTextBox.Text = string.Empty;
                if (_typingPushed)
                {
                    _typingPushed = false;
                    _ = _apiService?.PushVtsActivityAsync(false);
                }

                Messages.Add(new ChatMessageItem { Role = "user", Content = text });
                var assistantMsg = new ChatMessageItem { Role = "assistant", Content = "", IsStreaming = true };
                Messages.Add(assistantMsg);
                if (_voicePlayback?.AutoReadReplies == true)
                {
                    _voicePlayback.BeginReplyStream();
                }

                // Move input to bottom after first message
                if (_isFirstMessage)
                {
                    _isFirstMessage = false;
                    MoveInputToBottom(animated: true);
                }

                ScrollChatToBottom();

                await _wsClient.SendMessageAsync(text, _activeSessionId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in SendCurrentMessageAsync: {ex.Message}");
                _voicePlayback?.StopVoice();
                var last = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming);
                if (last != null)
                {
                    last.Content = $"**⚠️ LỖI GỬI:** {ex.Message}";
                    last.IsStreaming = false;
                    last.IsError = true;
                    int index = Messages.IndexOf(last);
                    Messages[index] = new ChatMessageItem
                    {
                        Id = last.Id,
                        Role = last.Role,
                        Content = last.Content,
                        Timestamp = last.Timestamp,
                        IsStreaming = false,
                        IsError = true
                    };
                }
                else
                {
                    Messages.Add(new ChatMessageItem
                    {
                        Role = "assistant",
                        Content = $"**⚠️ LỖI GỬI:** {ex.Message}",
                        IsStreaming = false,
                        IsError = true
                    });
                }
            }
        }

        private void ApplyInputWidth()
        {
            double contentWidth = Math.Max(
                0,
                Math.Min(InputMaxWidth, RootGrid.ActualWidth)
            );

            // Khung nhập.
            InputBorder.HorizontalAlignment = HorizontalAlignment.Center;
            InputBorder.Width = contentWidth;

            // Ép toàn bộ chuỗi Grid -> ScrollViewer -> ListView có cùng chiều rộng.
            // Nếu chỉ đặt Width cho ChatColumn, ListView vẫn có thể co theo nội dung.
            ChatColumn.HorizontalAlignment = HorizontalAlignment.Center;
            ChatColumn.Width = contentWidth;

            ChatScrollHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            ChatScrollHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ChatScrollHost.Width = contentWidth;

            ChatListView.HorizontalAlignment = HorizontalAlignment.Stretch;
            ChatListView.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ChatListView.Width = contentWidth;
        }

        private void UpdateCenterDelta()
        {
            if (RootGrid.ActualHeight <= 0 || InputContainer.ActualHeight <= 0) return;
            _centerDelta = Math.Max(0, (RootGrid.ActualHeight - InputContainer.ActualHeight) / 2);
            ApplyInputWidth();
            SetInputShift(-_centerDelta);
        }

        private void SetInputShift(double y)
        {
            if (InputContainer.RenderTransform is TranslateTransform existing)
            {
                existing.Y = y;
            }
            else
            {
                InputContainer.RenderTransform = new TranslateTransform { Y = y };
            }
        }

        private void CenterInput()
        {
            _inputCentered = true;
            Grid.SetRow(InputContainer, 0);
            InputContainer.VerticalAlignment = VerticalAlignment.Bottom;
            InputContainer.Padding = new Thickness(0, 0, 0, 24);
            UpdateCenterDelta();
        }

        private void MoveInputToBottom(bool animated = false)
        {
            _inputCentered = false;
            Grid.SetRow(InputContainer, 1);
            InputContainer.VerticalAlignment = VerticalAlignment.Stretch;
            InputContainer.Padding = new Thickness(0, 0, 0, 24);
            ApplyInputWidth();

            if (animated && _centerDelta > 0)
            {
                // Start from the previous centered spot and glide down.
                SetInputShift(-_centerDelta);
                var target = (TranslateTransform)InputContainer.RenderTransform;
                var storyboard = new Storyboard();
                var animation = new DoubleAnimation
                {
                    From = -_centerDelta,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(280),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(animation, target);
                Storyboard.SetTargetProperty(animation, "Y");
                storyboard.Children.Add(animation);
                storyboard.Begin();
            }
            else
            {
                SetInputShift(0);
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
            // Right after items are added the ListView has not measured yet, so
            // ScrollableHeight is still 0. Retry on the next layout passes.
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
                // Feed voice synthesis from the WebSocket callback directly. The
                // visual message can wait for the UI dispatcher without delaying
                // the first complete speech unit.
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
                            int index = Messages.IndexOf(last);
                            Messages[index] = new ChatMessageItem
                            {
                                Id = last.Id,
                                Role = last.Role,
                                Content = last.Content,
                                Timestamp = last.Timestamp,
                                IsStreaming = true
                            };
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
                                ? last.Content
                                : StripDisplayMarkers(content);
                            int index = Messages.IndexOf(last);
                            Messages[index] = new ChatMessageItem
                            {
                                Id = last.Id,
                                Role = last.Role,
                                Content = display,
                                Timestamp = last.Timestamp,
                                IsStreaming = false
                            };
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
                    }
                    catch (Exception ex)
                    {
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
                DispatcherQueue.TryEnqueue(() =>
                {
                    
                    var last = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming);
                    if (last == null)
                    {
                        Messages.Add(new ChatMessageItem
                        {
                            Role = "assistant",
                            Content = $"**⚠️ LỖI:** {error}\n\n{GetErrorDetails(error)}",
                            IsStreaming = false,
                            IsError = true
                        });
                        return;
                    }

                    try
                    {
                        string detailedError = $"**⚠️ LỖI: {error}**\n\n🔍 **Gợi ý sửa:** {GetErrorDetails(error)}";
                        last.Content = string.IsNullOrEmpty(last.Content)
                            ? detailedError
                            : $"{last.Content}\n\n{detailedError}";
                        last.IsStreaming = false;
                        last.IsError = true;
                        int index = Messages.IndexOf(last);
                        Messages[index] = new ChatMessageItem
                        {
                            Id = last.Id,
                            Role = last.Role,
                            Content = last.Content,
                            Timestamp = last.Timestamp,
                            IsStreaming = false,
                            IsError = true
                        };
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

                // Provide helpful context based on error type
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
                    return "Lỗi load model. Kiểm tra: Model file có tồn tại? Checkpoint path có đúng? GGUF files có đầy đủ?";
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
                    return $"Check backend logs at D:\\LLM\\logs\\server_err.log";
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
                _activeSessionId = sessionId;
                SessionOpened?.Invoke(this, new SessionOpenedEventArgs(sessionId, title));
                await _apiService.ActivateSessionAsync(_activeSessionId);

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
                    
                    // If session has messages, move input to bottom
                    if (history.Count > 0)
                    {
                        _isFirstMessage = false;
                        MoveInputToBottom();
                        ScrollChatToBottom();
                    }
                    else
                    {
                        _isFirstMessage = true;
                        CenterInput();
                    }
                }
                else
                {
                    _isFirstMessage = true;
                    CenterInput();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OpenSessionAsync: {ex.Message}");
                Messages.Add(new ChatMessageItem
                {
                    Role = "assistant",
                    Content = $"**⚠️ LỖI MỞ SESSION:** {ex.Message}",
                    IsError = true
                });
            }
        }

        private async void OnMicClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;

            if (_micRecorder.IsRecording)
            {
                MicButton.IsEnabled = false;
                var wav = await _micRecorder.StopAsync();
                MicIcon.Glyph = "\uE720";
                MicButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

                if (wav != null && wav.Length > 0)
                {
                    string? text = await _apiService.TranscribeAudioAsync(wav);
                    if (!string.IsNullOrEmpty(text))
                    {
                        ChatInputTextBox.Text = text;
                        ChatInputTextBox.Focus(FocusState.Programmatic);
                    }
                    else
                    {
                        ChatInputTextBox.PlaceholderText = "Không nghe rõ, thử lại nha.";
                    }
                }
                MicButton.IsEnabled = true;
                return;
            }

            bool ok = await _micRecorder.StartAsync();
            if (!ok)
            {
                ChatInputTextBox.PlaceholderText = "Không truy cập được micro. Kiểm tra quyền mic.";
                return;
            }
            MicIcon.Glyph = "\uE7C7";
            MicButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
        }
    }
}
