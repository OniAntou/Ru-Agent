using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RuApp.Services;

public enum VoiceConversationState
{
    Stopped,
    Listening,
    Transcribing,
    Sending,
    WaitingForReply,
    Error,
}

public sealed record VoiceConversationStatus(
    VoiceConversationState State,
    string? Message = null,
    Exception? Error = null);

/// <summary>
/// Owns the hands-free half-duplex loop: listen, transcribe, send, wait for Ru,
/// then listen again. The UI remains responsible for rendering messages and for
/// notifying this service when the WebSocket reply finishes.
/// </summary>
public sealed class VoiceConversationService : IDisposable
{
    private readonly IVoiceTurnCapture _capture;
    private readonly Func<byte[], CancellationToken, Task<string?>> _transcribe;
    private readonly Func<string, CancellationToken, Task> _sendText;
    private readonly object _gate = new();
    private CancellationTokenSource? _conversationCancellation;
    private Task? _conversationTask;
    private TaskCompletionSource<bool>? _replyCompletion;
    private bool _disposed;

    public VoiceConversationService(
        IVoiceTurnCapture capture,
        Func<byte[], CancellationToken, Task<string?>> transcribe,
        Func<string, CancellationToken, Task> sendText)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _transcribe = transcribe ?? throw new ArgumentNullException(nameof(transcribe));
        _sendText = sendText ?? throw new ArgumentNullException(nameof(sendText));
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _conversationCancellation != null;
            }
        }
    }

    public VoiceConversationState State { get; private set; } = VoiceConversationState.Stopped;

    public event EventHandler<VoiceConversationStatus>? StatusChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_conversationCancellation != null)
            {
                return Task.CompletedTask;
            }

            _conversationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _conversationTask = RunAsync(_conversationCancellation.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? conversation;
        lock (_gate)
        {
            cancellation = _conversationCancellation;
            conversation = _conversationTask;
        }

        if (cancellation == null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            await _capture.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            if (conversation != null)
            {
                await conversation.ConfigureAwait(false);
            }
        }
    }

    public void NotifyReplyCompleted()
    {
        lock (_gate)
        {
            _replyCompletion?.TrySetResult(true);
        }
    }

    public void NotifyReplyError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _replyCompletion?.TrySetException(exception);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _ = ObserveDisposeCleanupAsync();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            RaiseStatus(VoiceConversationState.Listening, "Đang nghe...");
            while (!cancellationToken.IsCancellationRequested)
            {
                VoiceTurnCaptureResult turn = await _capture
                    .CaptureTurnAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (turn.Reason == VoiceTurnStopReason.Cancelled || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (turn.Reason == VoiceTurnStopReason.NoSpeech)
                {
                    RaiseStatus(VoiceConversationState.Listening, "Đang nghe...");
                    continue;
                }

                if (turn.Reason == VoiceTurnStopReason.CaptureError || turn.WavBytes == null)
                {
                    RaiseStatus(
                        VoiceConversationState.Error,
                        "Không truy cập được micro.",
                        turn.Error);
                    break;
                }

                RaiseStatus(VoiceConversationState.Transcribing, "Đang hiểu lời anh...");
                string? text = await _transcribe(turn.WavBytes, cancellationToken)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    RaiseStatus(VoiceConversationState.Listening, "Không nghe rõ, anh nói lại nha.");
                    continue;
                }

                TaskCompletionSource<bool> replyCompletion = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    _replyCompletion = replyCompletion;
                }

                try
                {
                    RaiseStatus(VoiceConversationState.Sending, "Đang gửi cho Ru...");
                    await _sendText(text.Trim(), cancellationToken).ConfigureAwait(false);
                    RaiseStatus(VoiceConversationState.WaitingForReply, "Ru đang trả lời...");
                    await replyCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_replyCompletion, replyCompletion))
                        {
                            _replyCompletion = null;
                        }
                    }
                }

                RaiseStatus(VoiceConversationState.Listening, "Đang nghe...");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RaiseStatus(VoiceConversationState.Error, "Cuộc trò chuyện bằng giọng nói đã dừng.", ex);
        }
        finally
        {
            lock (_gate)
            {
                _replyCompletion = null;
                _conversationCancellation?.Dispose();
                _conversationCancellation = null;
                _conversationTask = null;
            }

            RaiseStatus(VoiceConversationState.Stopped);
        }
    }

    private async Task ObserveDisposeCleanupAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceConversation] Dispose cleanup failed: {ex.Message}");
        }
    }

    private void RaiseStatus(VoiceConversationState state, string? message = null, Exception? error = null)
    {
        State = state;
        try
        {
            StatusChanged?.Invoke(this, new VoiceConversationStatus(state, message, error));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceConversation] Status observer failed: {ex.Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VoiceConversationService));
        }
    }
}
