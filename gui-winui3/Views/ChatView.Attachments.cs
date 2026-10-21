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
                var intake = ChatAttachmentIntakePresenter.Evaluate(file.Name, checked((long)props.Size), Composer.ImageCount);
                if (!intake.Accepted)
                {
                    if (intake.Status is not null) ShowAgentInlineStatus(intake.Status);
                    return;
                }

                bool isImage = intake.IsImage;
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
                    var prepared = await PrepareImageAttachmentAsync(file.Name, bytes);
                    bytes = prepared.Bytes;
                    mimeType = prepared.MimeType;
                    previewImage = prepared.PreviewImage;
                }

                var item = new ImageAttachmentItem
                {
                    FileName = file.Name,
                    FilePath = file.Path,
                    ByteSize = bytes.LongLength,
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
                    item.TextContent = ChatAttachmentPolicy.DecodeText(bytes);
                }

                TryAddAttachmentToComposer(item, sessionId, sessionGeneration);
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error adding attachment: {ex.Message}");
            }
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
                    foreach (var storageItem in items)
                    {
                        if (storageItem is Windows.Storage.StorageFile file)
                        {
                            await AddFileAttachmentAsync(file, sessionId, sessionGeneration);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Clipboard image paste error: {ex.Message}");
            }
        }

        private bool CanAcceptImageAttachment() => Composer.CanAcceptImage();

        private bool TryAddAttachmentToComposer(
            ImageAttachmentItem attachment,
            string sessionId,
            long sessionGeneration)
        {
            if (!ChatAttachmentIntakePresenter.OwnsSession(sessionId, sessionGeneration, _activeSessionId, _sessionOpenGeneration))
            {
                return false;
            }

            return Composer.TryAddAttachment(sessionId, ChatInputTextBox.Text, attachment);
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
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
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
                    foreach (var storageItem in items)
                    {
                        if (storageItem is Windows.Storage.StorageFile file)
                        {
                            await AddFileAttachmentAsync(file, sessionId, sessionGeneration);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Error dropping files: {ex.Message}");
            }
        }
    }
}
