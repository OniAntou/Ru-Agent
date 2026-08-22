using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RuApp.Services
{
    public class ChatWebSocketClient : IDisposable
    {
        private const int ReconnectDelayMs = 2000;
        private const int ReconnectDelayMaxMs = 15000;

        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cts;
        private readonly BackendSession _session;
        private bool _disposed;
        private bool _backendStopped;

        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<string>? OnSessionReceived;
        public event Action<string>? OnDeltaReceived;
        public event Action? OnResetReceived;
        public event Action<string, string>? OnDoneReceived;
        public event Action<string>? OnErrorReceived;
        public event Action<string, string>? OnAgentActivityReceived;
        public event Action<AgentApprovalModel>? OnAgentApprovalRequired;
        public event Action<AgentApprovalResult>? OnAgentApprovalResult;

        public bool IsConnected => _webSocket != null && _webSocket.State == WebSocketState.Open;

        public ChatWebSocketClient(string authSecret, int port = 8000)
            : this(BackendSession.FromLoopback(port, authSecret))
        {
        }

        public ChatWebSocketClient(BackendSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public void NotifyBackendStopped()
        {
            Volatile.Write(ref _backendStopped, true);
        }

        public void Start()
        {
            if (_disposed) return;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => BackgroundLoopAsync(_cts.Token));
        }

        private async Task BackgroundLoopAsync(CancellationToken ct)
        {
            int reconnectDelayMs = ReconnectDelayMs;
            while (!ct.IsCancellationRequested && !Volatile.Read(ref _backendStopped))
            {
                try
                {
                    await ConnectAndListenAsync(ct);
                    reconnectDelayMs = ReconnectDelayMs;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ChatWebSocket] BackgroundLoop error: {ex.Message}");
                }

                if (ct.IsCancellationRequested || Volatile.Read(ref _backendStopped)) break;
                await Task.Delay(reconnectDelayMs, ct).ConfigureAwait(false);
                reconnectDelayMs = Math.Min(reconnectDelayMs * 2, ReconnectDelayMaxMs);
                if (!ct.IsCancellationRequested && !Volatile.Read(ref _backendStopped)) OnDisconnected?.Invoke();
            }
        }

        private async Task ConnectAndListenAsync(CancellationToken ct)
        {
            var uri = _session.ChatUri;
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, ct);
            if (ct.IsCancellationRequested || _disposed)
            {
                socket.Dispose();
                return;
            }

            var previous = _webSocket;
            _webSocket = socket;
            previous?.Dispose();
            OnConnected?.Invoke();

            byte[] buffer = new byte[8192];
            var ms = new System.IO.MemoryStream();

            try
            {
                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    string text = Encoding.UTF8.GetString(ms.ToArray());
                    HandleMessage(text);
                }
            }
            finally
            {
                if (ReferenceEquals(_webSocket, socket))
                {
                    _webSocket = null;
                }
                socket.Dispose();
            }
        }

        private void HandleMessage(string json)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"WebSocket received: {json}");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp))
                {
                    return;
                }
                string msgType = typeProp.GetString() ?? "";
                System.Diagnostics.Debug.WriteLine($"Message type: {msgType}");

                if (msgType == "session" && root.TryGetProperty("session_id", out var sessionProp))
                {
                    OnSessionReceived?.Invoke(sessionProp.GetString() ?? "");
                }
                else if (msgType == "chunk" && root.TryGetProperty("content", out var textProp))
                {
                    string content = textProp.GetString() ?? "";
                    System.Diagnostics.Debug.WriteLine($"Chunk content: {content.Substring(0, Math.Min(50, content.Length))}...");
                    OnDeltaReceived?.Invoke(content);
                }
                else if (msgType == "done")
                {
                    string content = root.TryGetProperty("content", out var doneProp)
                        ? doneProp.GetString() ?? ""
                        : "";
                    string sessionId = root.TryGetProperty("session_id", out var sidProp)
                        ? sidProp.GetString() ?? ""
                        : "";
                    System.Diagnostics.Debug.WriteLine($"Done message received, content length: {content.Length}");
                    OnDoneReceived?.Invoke(content, sessionId);
                }
                else if (msgType == "reset")
                {
                    System.Diagnostics.Debug.WriteLine("Reset message received");
                    OnResetReceived?.Invoke();
                }
                else if (msgType == "error")
                {
                    string content = root.TryGetProperty("content", out var errProp)
                        ? errProp.GetString() ?? "Lỗi không xác định"
                        : "Lỗi không xác định";
                    System.Diagnostics.Debug.WriteLine($"Error message: {content}");
                    OnErrorReceived?.Invoke(content);
                }
                else if (msgType == "agent.activity")
                {
                    string content = root.TryGetProperty("content", out var activityProp)
                        ? activityProp.GetString() ?? "Agent activity"
                        : "Agent activity";
                    string sessionId = root.TryGetProperty("session_id", out var activitySession)
                        ? activitySession.GetString() ?? ""
                        : "";
                    OnAgentActivityReceived?.Invoke(content, sessionId);
                }
                else if (msgType == "agent.approval_required")
                {
                    if (TryReadApproval(root, out var approval))
                    {
                        OnAgentApprovalRequired?.Invoke(approval);
                    }
                }
                else if (msgType == "agent.approval_result")
                {
                    if (TryReadApprovalResult(root, out var result))
                    {
                        OnAgentApprovalResult?.Invoke(result);
                    }
                }
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"JSON parse error: {ex.Message}");
                // Ignore malformed frames.
            }
        }

        private static bool TryReadApproval(JsonElement root, out AgentApprovalModel approval)
        {
            approval = default!;
            string approvalId = ReadBoundedString(root, "approval_id", 128);
            string sessionId = ReadBoundedString(root, "session_id", 128);
            string toolId = ReadBoundedString(root, "tool_id", 128);
            string safeAction = ReadBoundedString(root, "content", 256);
            string scope = ReadBoundedString(root, "scope", 256);
            string expiresText = ReadBoundedString(root, "expires_at", 64);

            if (string.IsNullOrWhiteSpace(approvalId)
                || string.IsNullOrWhiteSpace(sessionId)
                || string.IsNullOrWhiteSpace(toolId)
                || string.IsNullOrWhiteSpace(safeAction)
                || string.IsNullOrWhiteSpace(scope)
                || !DateTimeOffset.TryParse(
                    expiresText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var expiresAt)
                || expiresAt <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            approval = new AgentApprovalModel(
                approvalId,
                sessionId,
                toolId,
                safeAction,
                scope,
                expiresAt);
            return true;
        }

        private static bool TryReadApprovalResult(JsonElement root, out AgentApprovalResult result)
        {
            result = default!;
            string approvalId = ReadBoundedString(root, "approval_id", 128);
            string sessionId = ReadBoundedString(root, "session_id", 128);
            string status = ReadBoundedString(root, "status", 64);
            string safeSummary = ReadBoundedString(root, "content", 256);
            if (string.IsNullOrWhiteSpace(approvalId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            result = new AgentApprovalResult(
                approvalId,
                sessionId,
                string.IsNullOrWhiteSpace(status) ? "unknown" : status,
                string.IsNullOrWhiteSpace(safeSummary) ? "Approval updated." : safeSummary);
            return true;
        }

        private static string ReadBoundedString(JsonElement root, string propertyName, int maxLength)
        {
            if (!root.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            string value = property.GetString() ?? string.Empty;
            return value.Length <= maxLength ? value : string.Empty;
        }

        public async Task SendMessageAsync(string messageText, string sessionId)
        {
            await SendMessageAsync(messageText, sessionId, CancellationToken.None);
        }

        public async Task SendMessageAsync(
            string messageText,
            string sessionId,
            CancellationToken cancellationToken,
            bool agentMode = false)
        {
            var socket = await WaitForSocketAsync(cancellationToken);

            var correlationId = $"c_{Guid.NewGuid():N}";
            var payload = new
            {
                prompt = messageText,
                session_id = sessionId,
                correlation_id = correlationId,
                agent_mode = agentMode,
            };
            string json = JsonSerializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }

        public async Task SendAgentApprovalAsync(
            string sessionId,
            string approvalId,
            bool approved,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(approvalId))
            {
                throw new ArgumentException("Approval session and ID are required.");
            }

            var socket = await WaitForSocketAsync(cancellationToken);
            var payload = new
            {
                type = "agent.approval.response",
                session_id = sessionId,
                approval_id = approvalId,
                approved,
            };
            string json = JsonSerializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }

        private async Task<ClientWebSocket> WaitForSocketAsync(CancellationToken cancellationToken)
        {
            const int connectTimeoutMs = 10000;
            var startTime = DateTime.UtcNow;
            while (!IsConnected && !_disposed)
            {
                await Task.Delay(250, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if ((DateTime.UtcNow - startTime).TotalMilliseconds > connectTimeoutMs)
                {
                    throw new InvalidOperationException("WebSocket chưa kết nối trong vòng 10 giây.");
                }
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ChatWebSocketClient));
            }

            return _webSocket ?? throw new InvalidOperationException("WebSocket không khả dụng.");
        }

        public void Disconnect()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            try
            {
                if (_webSocket != null)
                {
                    if (_webSocket.State == WebSocketState.Open)
                    {
                        try
                        {
                            _webSocket.Abort();
                        }
                        catch
                        {
                        }
                    }
                    _webSocket.Dispose();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            _webSocket = null;
            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
