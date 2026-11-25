using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    /// <summary>
    /// A reopened chat shows the images its user messages carried, not only their count.
    /// History keeps the count; the images themselves stay encrypted on the Host and are read
    /// back here, one at a time, decoded straight to a thumbnail. Any failure leaves the
    /// "N images attached" label, and opening the chat never waits on it.
    /// </summary>
    public sealed partial class ChatView
    {
        private readonly HashSet<ChatMessageItem> _imageHydrationStarted = new(ReferenceEqualityComparer.Instance);

        /// <summary>Starts loading images for shown messages that only have their count.</summary>
        private void HydrateReplayedImages()
        {
            var pending = Messages
                .Where(message => message.Role == "user" && message.MissingImageCount > 0
                    && !string.IsNullOrEmpty(message.SourceRunId))
                .Where(message => _imageHydrationStarted.Add(message))
                .ToList();
            if (pending.Count == 0) return;
            _ = HydrateReplayedImagesAsync(pending, _activeSessionId, _sessionOpenGeneration);
        }

        private async Task HydrateReplayedImagesAsync(
            IReadOnlyList<ChatMessageItem> pending, string? sessionId, long generation)
        {
            bool StillOpen() => string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)
                && _sessionOpenGeneration == generation;
            if (_apiService is not { } api) return;
            try
            {
                foreach (var run in pending.GroupBy(message => message.SourceRunId!, StringComparer.Ordinal))
                {
                    if (!StillOpen()) return;
                    var images = await api.GetRunImagePayloadsAsync(run.Key, CancellationToken.None);
                    if (images is not { Count: > 0 }) continue;
                    foreach (var message in run)
                    {
                        var number = 0;
                        foreach (var payloadId in ReplayImageAssignment.PayloadsFor(message, images))
                        {
                            if (!StillOpen()) return;
                            var dataUrl = await api.ReadImageDataUrlAsync(run.Key, payloadId, CancellationToken.None);
                            if (ReplayImageAssignment.DecodeDataUrl(dataUrl) is not { } image || !StillOpen()) continue;
                            var item = new ImageAttachmentItem
                            {
                                IsImage = true,
                                FileName = $"Image {++number}",
                                MimeType = image.MimeType,
                                ByteSize = image.Bytes.Length,
                                RemoteRunId = run.Key,
                                RemotePayloadId = payloadId,
                            };
                            // Only the thumbnail is kept; the full image is read again when opened.
                            await item.LoadPreviewImageAsync(image.Bytes);
                            message.Attachments ??= new System.Collections.ObjectModel.ObservableCollection<ImageAttachmentItem>();
                            message.Attachments.Add(item);
                            message.MissingImageCount = Math.Max(0, message.MissingImageCount - 1);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] replayed images could not be loaded: {ex.GetType().Name}");
            }
        }

        /// <summary>The full image behind a reopened thumbnail, read again from the Host.</summary>
        private async Task<byte[]?> ReadRemoteImageBytesAsync(ImageAttachmentItem item)
        {
            if (item.RemoteRunId is not { } runId || item.RemotePayloadId is not { } payloadId
                || _apiService is not { } api) return null;
            try
            {
                var dataUrl = await api.ReadImageDataUrlAsync(runId, payloadId, CancellationToken.None);
                return ReplayImageAssignment.DecodeDataUrl(dataUrl)?.Bytes;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] image could not be read: {ex.GetType().Name}");
                return null;
            }
        }
    }
}
