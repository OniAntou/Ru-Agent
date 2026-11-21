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
    public sealed partial class ChatView
    {
        /// <summary>
        /// Reads one picked file into an attachment. Images are bounded by the
        /// image cap and decoded for the preview thumbnail; text and code files
        /// are bounded by the text cap and read as UTF-8.
        /// </summary>
        private async Task AddFileAttachmentAsync(
            Windows.Storage.StorageFile file,
            string sessionId,
            long sessionGeneration)
        {
            try
            {
                var props = await file.GetBasicPropertiesAsync();
                // A folderless chat has no workspace yet, but the Host can allocate the
                // Ru-owned scratch workspace before the first run. Ask for it before a
                // non-image file is classified, so any type can be staged by path instead
                // of falling back to "choose a folder". Images keep their image path.
                if (ChatAttachmentPolicy.NeedsScratchWorkspace(file.Name, _agentWorkspace))
                {
                    try
                    {
                        await EnsureScratchWorkspaceForAttachmentsAsync(
                            sessionId, sessionGeneration, _sessionPresentation.SessionToken);
                    }
                    catch (OperationCanceledException) when (_sessionPresentation.SessionToken.IsCancellationRequested)
                    {
                        return;
                    }
                }

                if (await TryAddFileReferenceAsync(file, checked((long)props.Size), forceReference: false, sessionId, sessionGeneration))
                {
                    return;
                }

                var intake = ChatAttachmentIntakePresenter.Evaluate(file.Name, checked((long)props.Size), Composer.ImageCount);
                if (!intake.Accepted)
                {
                    if (intake.Status is not null) ShowAgentInlineStatus(intake.Status);
                    return;
                }

                bool isImage = intake.IsImage;
                bool isDocument = intake.IsDocument;
                int maximumBytes = intake.MaximumBytes;

                using var readStream = (await file.OpenReadAsync()).AsStreamForRead();
                byte[]? bytes = await ReadBoundedBytesAsync(readStream, maximumBytes);
                if (bytes is null)
                {
                    ShowAgentInlineStatus($"File '{file.Name}' could not be read within the attachment limit.");
                    return;
                }

                string mimeType = "text/plain";
                Microsoft.UI.Xaml.Media.Imaging.BitmapImage? previewImage = null;

                if (isImage)
                {
                    try
                    {
                        var prepared = await PrepareImageAttachmentAsync(file.Name, bytes);
                        bytes = prepared.Bytes;
                        mimeType = prepared.MimeType;
                        previewImage = prepared.PreviewImage;
                    }
                    catch (InvalidDataException error)
                    {
                        // Normalization errors are named (bounded decode, dimensions, wire
                        // format). A generic "file may be damaged" would mislead the user
                        // about an image that is valid but larger than the wire bound.
                        ShowAgentInlineStatus($"'{file.Name}' could not be attached: {error.Message}");
                        return;
                    }
                }
                else if (isDocument)
                {
                    var sessionToken = _sessionPresentation.SessionToken;
                    try
                    {
                        // A long PDF can take seconds; switching session stops it.
                        string extracted = await Task.Run(
                            () => AttachmentDocumentExtractor.Extract(file.Name, bytes, sessionToken), sessionToken);
                        bytes = System.Text.Encoding.UTF8.GetBytes(extracted);
                    }
                    catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (AttachmentExtractionException error)
                    {
                        ShowAgentInlineStatus(error.Message);
                        return;
                    }
                }

                var item = new ImageAttachmentItem
                {
                    FileName = file.Name,
                    FilePath = file.Path,
                    ByteSize = isDocument ? checked((long)props.Size) : bytes.LongLength,
                    IsImage = isImage,
                    MimeType = mimeType,
                };

                if (isImage)
                {
                    item.Base64Data = Convert.ToBase64String(bytes);
                    item.PreviewImage = previewImage;
                }
                else
                {
                    var text = ChatAttachmentPolicy.DecodeText(bytes);
                    if (text is null)
                    {
                        // Not UTF-8: send the path, and `read` tells the model how to decode it.
                        if (!await TryAddFileReferenceAsync(file, checked((long)props.Size), forceReference: true, sessionId, sessionGeneration))
                        {
                            ShowAgentInlineStatus($"{file.Name} is not UTF-8 text. Choose a project folder to attach it as a file the agent reads, or save it as UTF-8.");
                        }

                        return;
                    }

                    item.TextContent = text;
                }

                if (!isImage
                    && !ChatAttachmentFitsPrompt(item))
                {
                    return;
                }

                TryAddAttachmentToComposer(item, sessionId, sessionGeneration);
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error adding attachment: {ex.Message}");
                ReportAttachmentIntakeFailure(
                    $"Could not attach '{file.Name}'. Check that the file is readable and the image is not damaged.",
                    sessionId, sessionGeneration);
            }
        }

        /// <summary>
        /// A chat with no project folder still needs a real workspace before it can stage
        /// an arbitrary attachment. The Host owns the path: it creates the session-scoped
        /// scratch folder and returns it, and the client only uses that path. The same
        /// path is sent with the first run, where the Host's marker still classifies it
        /// as scratch for trust and display.
        /// </summary>
        private async Task<bool> EnsureScratchWorkspaceForAttachmentsAsync(
            string sessionId,
            long sessionGeneration,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(_agentWorkspace)) return true;
            if (_apiService is null || !AgentSessionSelection.IsValid(sessionId)) return false;

            ScratchWorkspacePath? scratch;
            try
            {
                scratch = await _apiService.EnsureScratchWorkspaceAsync(sessionId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            if (scratch is null || string.IsNullOrWhiteSpace(scratch.Path)) return false;
            if (!ChatAttachmentIntakePresenter.OwnsSession(
                    sessionId, sessionGeneration, _activeSessionId, _sessionOpenGeneration))
            {
                return false;
            }

            SetRequestedWorkspace(scratch.Path, isScratch: true);
            SetAgentWorkspace(scratch.Path, isScratch: true);
            _requestedWorkspaceBySession[sessionId] = scratch.Path;
            UpdateChatHeaderWorkspace();
            return true;
        }

        /// <summary>
        /// Ctrl+V: a clipboard bitmap becomes a screenshot attachment, clipboard
        /// files go through the same path as the file picker.
        /// </summary>
        private async Task TryPasteImageFromClipboardAsync()
        {
            string sessionId = _activeSessionId;
            long sessionGeneration = _sessionOpenGeneration;
            try
            {
                var package = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
                {
                    if (!CanAcceptImageAttachment())
                    {
                        return;
                    }

                    var streamRef = await package.GetBitmapAsync();
                    using var stream = await streamRef.OpenReadAsync();
                    using var readStream = stream.AsStreamForRead();
                    byte[]? bytes = await ReadBoundedBytesAsync(readStream, MaxImageAttachmentBytes);
                    if (bytes is null)
                    {
                        ShowAgentInlineStatus("The pasted image exceeds the attachment limit.");
                        return;
                    }

                    string fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    var prepared = await PrepareImageAttachmentAsync(fileName, bytes);

                    TryAddAttachmentToComposer(new ImageAttachmentItem
                    {
                        FileName = fileName,
                        ByteSize = prepared.Bytes.LongLength,
                        Base64Data = Convert.ToBase64String(prepared.Bytes),
                        MimeType = prepared.MimeType,
                        IsImage = true,
                        PreviewImage = prepared.PreviewImage,
                    }, sessionId, sessionGeneration);
                }
                else if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                {
                    var items = await package.GetStorageItemsAsync();
                    bool sawFolder = false;
                    foreach (var storageItem in items)
                    {
                        if (storageItem is Windows.Storage.StorageFile file)
                        {
                            await AddFileAttachmentAsync(file, sessionId, sessionGeneration);
                        }
                        else if (storageItem is Windows.Storage.StorageFolder)
                        {
                            sawFolder = true;
                        }
                    }

                    if (sawFolder)
                    {
                        ShowAgentInlineStatus("Folders are not attached. Drag the files inside the folder, or open the Files tab.");
                    }
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Clipboard image paste error: {ex.Message}");
                ReportAttachmentIntakeFailure(
                    "Could not paste the image. Try saving it as PNG or JPEG and attaching the file.",
                    sessionId, sessionGeneration);
            }
        }

        private bool CanAcceptImageAttachment() => Composer.CanAcceptImage();

        private void ReportAttachmentIntakeFailure(string status, string sessionId, long sessionGeneration)
        {
            if (ChatAttachmentIntakePresenter.OwnsSession(sessionId, sessionGeneration, _activeSessionId, _sessionOpenGeneration))
            {
                ShowAgentInlineStatus(status);
            }
        }

        private bool TryAddAttachmentToComposer(
            ImageAttachmentItem attachment,
            string sessionId,
            long sessionGeneration)
        {
            if (!ChatAttachmentIntakePresenter.OwnsSession(sessionId, sessionGeneration, _activeSessionId, _sessionOpenGeneration))
            {
                return false;
            }

            bool added = Composer.TryAddAttachment(sessionId, ChatInputTextBox.Text, attachment);
            // Say it before Send: a text-only model receives a notice instead of the image
            // (ADR 0040), which reads like the image was ignored.
            if (added && attachment.IsImage && ModelSelection.AcceptsImages() == false)
            {
                ShowAgentInlineStatus(
                    $"{ModelSelection.DisplayModel} cannot read images; it will get a note that an image was attached. Pick a vision model to send the image itself.");
            }

            return added;
        }

        /// <summary>
        /// Attaches a file by its workspace path for the model to open with <c>read</c>
        /// (ADR 0053): any type and any size. A file outside the workspace is copied into
        /// <c>.ru\attachments</c> first. Only small UTF-8 text is inlined, and an image goes as
        /// an image to a model that reads images; those return false here unless
        /// <paramref name="forceReference"/> is set. Without a workspace yet (a new chat with no
        /// folder) there is nowhere to put the file, so the caller inlines what it can.
        /// </summary>
        private async Task<bool> TryAddFileReferenceAsync(
            Windows.Storage.StorageFile file,
            long size,
            bool forceReference,
            string sessionId,
            long sessionGeneration)
        {
            const long referenceThresholdBytes = RuAgent.Client.Attachments.AttachmentStaging.InlineTextThresholdBytes;
            bool isImage = ChatAttachmentPolicy.IsImageFile(file.Name);
            bool sendsAsImage = isImage
                && ModelSelection.AcceptsImages() != false
                && size <= ChatAttachmentPolicy.MaxImageAttachmentBytes;
            bool inlinesAsText = !isImage && ChatAttachmentPolicy.IsTextOrCodeFile(file.Name) && size <= referenceThresholdBytes;
            string workspace = _agentWorkspace;
            if ((!forceReference && (sendsAsImage || inlinesAsText)) || string.IsNullOrWhiteSpace(workspace))
            {
                return false;
            }

            bool staged = false;
            if (!TryGetWorkspaceRelativePath(workspace, file.Path, out var relative))
            {
                try
                {
                    using var source = (await file.OpenReadAsync()).AsStreamForRead();
                    var copy = await RuAgent.Client.Attachments.AttachmentStaging.StageAsync(
                        source, file.Name, workspace, _sessionPresentation.SessionToken);
                    relative = copy.RelativePath;
                    size = copy.ByteSize;
                    staged = true;
                }
                catch (RuAgent.Client.Attachments.AttachmentStagingException error)
                {
                    ReportAttachmentIntakeFailure($"{file.Name} could not be attached: {error.Message}.", sessionId, sessionGeneration);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return true;
                }
            }

            var item = new ImageAttachmentItem
            {
                FileName = file.Name,
                FilePath = file.Path,
                ByteSize = size,
                IsImage = false,
                MimeType = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType,
                ReferencePath = relative,
                IsStagedCopy = staged,
            };
            if (!ChatAttachmentFitsPrompt(item) || !TryAddAttachmentToComposer(item, sessionId, sessionGeneration))
            {
                if (staged) RuAgent.Client.Attachments.AttachmentStaging.TryRemove(relative, workspace);
                return true;
            }

            if (isImage)
            {
                ShowAgentInlineStatus(
                    $"{ModelSelection.DisplayModel} cannot read images, so {file.Name} is attached as a file path. Pick a vision model to send the image itself.");
            }

            return true;
        }

        private static bool TryGetWorkspaceRelativePath(string? workspace, string? filePath, out string relative)
        {
            relative = string.Empty;
            if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            try
            {
                string root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(filePath);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                return relative.Length > 0;
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>
        /// A document that cannot fit the Host prompt bound is refused at intake
        /// instead of being accepted and then rejected on Send.
        /// </summary>
        private bool ChatAttachmentFitsPrompt(ImageAttachmentItem attachment)
        {
            var attachments = Composer.Attachments.ToList();
            attachments.Add(attachment);
            string prompt = ChatComposerViewModel.BuildEffectivePrompt(ChatInputTextBox.Text, attachments);
            if (ChatComposerViewModel.PromptLimitStatus(prompt) is not { } status)
            {
                return true;
            }

            ShowAgentInlineStatus(status + " Remove some text or attach a smaller file.");
            return false;
        }

        private sealed record PreparedImageAttachment(
            byte[] Bytes,
            string MimeType,
            Microsoft.UI.Xaml.Media.Imaging.BitmapImage PreviewImage);

        private async Task<PreparedImageAttachment> PrepareImageAttachmentAsync(
            string fileName,
            byte[] sourceBytes)
        {
            using var original = new MemoryStream(sourceBytes, writable: false);
            using var originalStream = original.AsRandomAccessStream();
            var resized = await ImageAttachmentResizer.ResizeIfNeededAsync(
                originalStream,
                ChatAttachmentPolicy.MimeForExtension(fileName));
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = ImageAttachmentItem.ThumbnailDecodeWidth };
            using var previewBytes = new MemoryStream(resized.Bytes, writable: false);
            using var previewStream = previewBytes.AsRandomAccessStream();
            await bitmap.SetSourceAsync(previewStream);
            return new PreparedImageAttachment(resized.Bytes, resized.MimeType, bitmap);
        }


        private async void OnAttachImageClick(object sender, RoutedEventArgs e)
        {
            string sessionId = _activeSessionId;
            long sessionGeneration = _sessionOpenGeneration;
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add("*");

                if (MainWindow.Instance != null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                }

                var files = await picker.PickMultipleFilesAsync();
                if (files != null && files.Count > 0)
                {
                    foreach (var file in files)
                    {
                        await AddFileAttachmentAsync(file, sessionId, sessionGeneration);
                    }
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error picking files: {ex.Message}");
                ReportAttachmentIntakeFailure(
                    "Could not open the file picker. Try dragging a file into the composer.",
                    sessionId, sessionGeneration);
            }
        }

        /// <summary>
        /// Drag-and-drop: every dropped storage file goes through the same
        /// attachment path as the picker, so the size and type limits apply once.
        /// </summary>
        private async void OnInputDrop(object sender, DragEventArgs e)
        {
            string sessionId = _activeSessionId;
            long sessionGeneration = _sessionOpenGeneration;
            try
            {
                if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    bool sawFolder = false;
                    foreach (var storageItem in items)
                    {
                        if (storageItem is Windows.Storage.StorageFile file)
                        {
                            await AddFileAttachmentAsync(file, sessionId, sessionGeneration);
                        }
                        else if (storageItem is Windows.Storage.StorageFolder)
                        {
                            sawFolder = true;
                        }
                    }

                    if (sawFolder)
                    {
                        ShowAgentInlineStatus("Folders are not attached. Drop the files inside the folder, or open the Files tab.");
                    }
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error dropping files: {ex.Message}");
                ReportAttachmentIntakeFailure(
                    "Could not read the dropped files. Try dropping them again.",
                    sessionId, sessionGeneration);
            }
        }
    }
}
