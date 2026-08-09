using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RuApp.Services;

/// <summary>
/// Delivers best-effort subtitle state without putting presenter or UI work on
/// the voice playback path.
/// </summary>
public sealed class VoiceSubtitleEventPump : IDisposable
{
    private readonly SubtitlePresenter _presenter;
    private readonly Channel<SubtitleUpdate> _mailbox;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _consumer;
    private string? _latestSegmentId;
    private int _enabled;
    private int _disposed;

    public VoiceSubtitleEventPump(SubtitlePresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _mailbox = Channel.CreateBounded<SubtitleUpdate>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        _consumer = Task.Run(() => ConsumeAsync(_cancellation.Token));
    }

    public bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Interlocked.Exchange(ref _enabled, enabled ? 1 : 0);
        if (enabled)
        {
            return;
        }

        Interlocked.Exchange(ref _latestSegmentId, null);
        while (_mailbox.Reader.TryRead(out _))
        {
        }
        TryCancelPresenter();
    }

    public bool TryShow(VoiceSubtitleCue cue)
    {
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        Volatile.Write(ref _latestSegmentId, cue.Id);
        return _mailbox.Writer.TryWrite(SubtitleUpdate.Show(cue));
    }

    public bool TryHide(string segmentId)
    {
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        string? current = Volatile.Read(ref _latestSegmentId);
        if (current is null ||
            !string.Equals(current, segmentId, StringComparison.Ordinal) ||
            !ReferenceEquals(
                Interlocked.CompareExchange(ref _latestSegmentId, null, current),
                current))
        {
            return false;
        }

        return _mailbox.Writer.TryWrite(SubtitleUpdate.Hide(segmentId));
    }

    public bool TryClear()
    {
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        Interlocked.Exchange(ref _latestSegmentId, null);
        return _mailbox.Writer.TryWrite(SubtitleUpdate.Clear());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _enabled, 0);
        Interlocked.Exchange(ref _latestSegmentId, null);
        _mailbox.Writer.TryComplete();
        _cancellation.Cancel();
        TryCancelPresenter();
        _cancellation.Dispose();
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (SubtitleUpdate update in
                _mailbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsEnabled || !IsCurrent(update))
                {
                    continue;
                }

                try
                {
                    switch (update.Kind)
                    {
                        case SubtitleUpdateKind.Show:
                            _presenter.BeginSegment(update.SegmentId, update.Text);
                            break;
                        case SubtitleUpdateKind.Hide:
                            _presenter.EndSegment(update.SegmentId);
                            break;
                        case SubtitleUpdateKind.Clear:
                            _presenter.CancelAll();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VoiceSubtitleEventPump] Presenter failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceSubtitleEventPump] Worker failed: {ex.Message}");
        }
    }

    private bool IsCurrent(SubtitleUpdate update)
    {
        string? latest = Volatile.Read(ref _latestSegmentId);
        return update.Kind switch
        {
            SubtitleUpdateKind.Show => string.Equals(
                latest, update.SegmentId, StringComparison.Ordinal),
            SubtitleUpdateKind.Hide or SubtitleUpdateKind.Clear => latest is null,
            _ => false,
        };
    }

    private void TryCancelPresenter()
    {
        try
        {
            _presenter.CancelAll();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceSubtitleEventPump] Presenter clear failed: {ex.Message}");
        }
    }

    private enum SubtitleUpdateKind
    {
        Show,
        Hide,
        Clear,
    }

    private readonly record struct SubtitleUpdate(
        SubtitleUpdateKind Kind,
        string SegmentId,
        string Text)
    {
        public static SubtitleUpdate Show(VoiceSubtitleCue cue) =>
            new(SubtitleUpdateKind.Show, cue.Id, cue.Text);

        public static SubtitleUpdate Hide(string segmentId) =>
            new(SubtitleUpdateKind.Hide, segmentId, "");

        public static SubtitleUpdate Clear() =>
            new(SubtitleUpdateKind.Clear, "", "");
    }
}
