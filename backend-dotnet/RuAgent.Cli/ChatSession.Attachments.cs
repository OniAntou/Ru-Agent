using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Cli.Attachments;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;


namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        void HoldSubmissionForRetry(ChatSubmissionSnapshot? submission)
        {
            if (submission is null)
            {
                return;
            }

            submissions.HoldForRetry(submission);
            RefreshAttachmentLabels();
            chatEngine?.PauseQueuedSubmissions();
        }

        static void DeleteTempPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temp cleanup is best-effort; the file lives under %TEMP%.
            }
        }

        static void DeleteTempFile(Images.CliImageAttachment attachment) => ChatSubmissionState.Release([attachment]);

        string CurrentAttachmentLabel()
        {
            var count = submissions.Count;
            return count == 0
                ? workspaceLabel
                : $"[{count} attachment{(count == 1 ? string.Empty : "s")}] {workspaceLabel}";
        }

        void RefreshAttachmentLabels()
        {
            chatEngine?.RefreshAttachmentLabel();
        }

        async Task AttachImageAsync(string path, string? tempPath = null)
        {
            var attached = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pendingImages.Count >= Images.CliImagePolicy.MaxImageCount)
                {
                    if (tgMode) ShowPasteFeedback(Images.CliImagePolicy.TooManyImagesStatus);
                    await WriteDiagnosticLineAsync(humanOut, Images.CliImagePolicy.TooManyImagesStatus).ConfigureAwait(false);
                    return;
                }

                var (attachment, imageError) = await Images.CliImageFiles
                    .LoadAsync(path, canonicalWorkspace, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (attachment is null)
                {
                    await WriteAttachFeedbackAsync(imageError ?? "invalid_image: the attachment could not be loaded").ConfigureAwait(false);
                    return;
                }

                var totalBytes = pendingImages.Sum(image => image.ByteSize) + attachment.ByteSize;
                if (totalBytes > Images.CliImagePolicy.MaxTotalImageBytes)
                {
                    await WriteAttachFeedbackAsync(
                        $"image_too_large: this turn's images would total {Images.CliImageAttachment.FormatBytes(totalBytes)} (limit {Images.CliImageAttachment.FormatBytes(Images.CliImagePolicy.MaxTotalImageBytes)}); this image was not added").ConfigureAwait(false);
                    return;
                }

                if (tempPath is not null)
                {
                    attachment = attachment with { TempPath = tempPath };
                }

                submissions.Add(attachment);
                attached = true; // The pending state now owns cleanup of this image's temp file.
                RefreshAttachmentLabels();
                if (activeModel.Length > 0 && !Images.CliVisionModels.IsVisionModel(activeModel))
                {
                    await WriteAttachFeedbackAsync(
                        $"model_vision_unsupported: {activeModel} may not read images; switch to a vision model with /model").ConfigureAwait(false);
                }

                var attachedMessage = $"attached [{submissions.Count}] {attachment.Label}";
                if (tgMode)
                {
                    ShowPasteFeedback(attachedMessage, false);
                }

                await WriteDiagnosticLineAsync(humanOut, attachedMessage).ConfigureAwait(false);
            }
            finally
            {
                if (!attached) DeleteTempPath(tempPath);
            }
        }

        /// <summary>
        /// Attachment failures must be visible with a live TUI: stderr lands behind the frame,
        /// so the hint row mirrors the message there (classic mode keeps stderr only).
        /// </summary>
        async Task WriteAttachFeedbackAsync(string message)
        {
            if (tgMode)
            {
                ShowPasteFeedback(message);
            }

            await WriteDiagnosticLineAsync(_err, message).ConfigureAwait(false);
        }

        async Task AttachFileAsync(string path)
        {
            if (Images.CliImagePolicy.IsImageFile(path))
            {
                await AttachImageAsync(path).ConfigureAwait(false);
                return;
            }

            var (attachment, textError) = await Attachments.CliTextFiles
                .LoadAsync(path, canonicalWorkspace, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (attachment is null)
            {
                await WriteAttachFeedbackAsync(textError ?? "unsupported_attachment: the file could not be loaded").ConfigureAwait(false);
                return;
            }

            submissions.Add(attachment);
            RefreshAttachmentLabels();
            await WriteDiagnosticLineAsync(humanOut, $"attached [{submissions.Count}] {attachment.Label}").ConfigureAwait(false);
        }

        /// <summary>
        /// Inserts pasted text at the TG cursor on the UI loop; the composer itself never
        /// sees the Ctrl+V (the clipboard read runs here), so the two cannot double-paste.
        /// </summary>
        void InsertIntoComposer(string text)
        {
            try
            {
                tgApp!.Invoke(() =>
                {
                    if (cancellationToken.IsCancellationRequested || attachmentTasks.IsStopped) return;
                    var input = tgWindow!.Composer.Input;
                    if (!input.TryInsertDraft(text))
                    {
                        tgWindow.Hints.ShowFeedback("paste too large · draft unchanged · limit 16000 characters", true);
                    }
                    else
                    {
                        TuiDebug.Log("paste", $"inserted {text.Length} characters at the composer cursor");
                    }
                });
            }
            catch (NotInitializedException)
            {
                TuiDebug.Log("paste", "tg session wound down before the paste landed");
            }
        }

        void ShowPasteFeedback(string message, bool isError = true)
        {
            try
            {
                tgApp?.Invoke(() =>
                {
                    if (!cancellationToken.IsCancellationRequested && !attachmentTasks.IsStopped)
                        tgWindow?.Hints.ShowFeedback(message, isError);
                });
            }
            catch (NotInitializedException)
            {
                // The TG session wound down before the feedback landed.
            }
        }

        async Task ClipboardAttachAsync()
        {
            var clipboard = Images.CliClipboard.TryReadImagePath();
            TuiDebug.Log("paste", $"image read: path={clipboard.Path} error={clipboard.Error}");
            if (clipboard.Path is null)
            {
                // A text-only clipboard still pastes: Ctrl+V would otherwise swallow the
                // keystroke with only an error line.
                if (tgMode)
                {
                    var text = Images.CliClipboard.TryReadText();
                    cancellationToken.ThrowIfCancellationRequested();
                    TuiDebug.Log("paste", $"text read: length={text?.Length ?? 0}");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        InsertIntoComposer(text);
                        return;
                    }

                    // The diagnostic line writes behind the live TUI; the hint row is the
                    // feedback the composer can actually see.
                    ShowPasteFeedback(clipboard.Error ?? "clipboard_has_no_image: copy a screenshot or an image file first");
                    return;
                }

                await WriteDiagnosticLineAsync(_err, clipboard.Error ?? "clipboard_has_no_image: copy a screenshot or an image file first").ConfigureAwait(false);
                return;
            }

            var name = Path.GetFileName(clipboard.Path);
            var isTemp = name.StartsWith("ru-img-", StringComparison.Ordinal)
                && string.Equals(Path.GetDirectoryName(clipboard.Path), Path.GetTempPath().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            await AttachImageAsync(clipboard.Path, isTemp ? clipboard.Path : null).ConfigureAwait(false);
        }

        bool IsDroppedAttachmentPath(string candidate)
        {
            if (candidate.Length > AgentCliParser.MaxFilePathLength)
            {
                return false;
            }

            // A bare file name ("README.md") stays a prompt: only a rooted path or one
            // with a separator reads as a drop, so ordinary prose cannot attach by accident.
            if (!Path.IsPathRooted(candidate)
                && !candidate.Contains('/')
                && !candidate.Contains('\\'))
            {
                return false;
            }

            if (!Images.CliImagePolicy.IsImageFile(candidate)
                && !Images.CliImagePolicy.IsTextOrCodeFile(candidate))
            {
                return false;
            }

            try
            {
                return File.Exists(Images.CliImageFiles.ResolvePath(candidate, canonicalWorkspace));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }
}
