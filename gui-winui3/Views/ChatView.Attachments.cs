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
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
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
        private async Task AddFileAttachmentAsync(Windows.Storage.StorageFile file)
        {
            try
            {
                var props = await file.GetBasicPropertiesAsync();
                bool isImage = IsImageFile(file.Name);
                if (!isImage && !IsTextOrCodeFile(file.Name))
                {
                    ShowAgentInlineStatus($"File '{file.Name}' is not supported. Only images and text/code files are accepted.");
                    return;
                }

                int maximumBytes = isImage ? MaxImageAttachmentBytes : MaxTextAttachmentBytes;
                if (props.Size > (ulong)maximumBytes)
                {
                    ShowAgentInlineStatus(isImage
                        ? $"Image '{file.Name}' exceeds the {maximumBytes / (1024 * 1024)} MB attachment limit."
                        : $"File '{file.Name}' exceeds the {maximumBytes / (1024 * 1024)} MB attachment limit.");
                    return;
                }

                using var readStream = (await file.OpenReadAsync()).AsStreamForRead();
                byte[]? bytes = await ReadBoundedBytesAsync(readStream, maximumBytes);
                if (bytes is null)
                {
                    ShowAgentInlineStatus($"File '{file.Name}' could not be read within the attachment limit.");
                    return;
                }

                string mimeType = isImage
                    ? Services.ChatAttachmentPolicy.MimeForExtension(file.Name)
                    : "text/plain";

                if (isImage)
                {
                    // Images are normalized on the client: over-large dimensions are
                    // scaled down and the backend's decoded-size limit is enforced
                    // before the bytes ever leave the desktop.
                    using var original = new System.IO.MemoryStream(bytes);
                    using var originalStream = original.AsRandomAccessStream();
                    var resized = await Services.ImageAttachmentResizer.ResizeIfNeededAsync(originalStream, mimeType);
                    bytes = resized.Bytes;
                    mimeType = resized.MimeType;
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
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    using var previewBytes = new System.IO.MemoryStream(bytes);
                    using var previewStream = previewBytes.AsRandomAccessStream();
                    await bitmap.SetSourceAsync(previewStream);
                    item.PreviewImage = bitmap;
                }
                else
                {
                    item.TextContent = System.Text.Encoding.UTF8.GetString(bytes);
                }

                PendingAttachments.Add(item);
                AttachmentPreviewContainer.Visibility = Visibility.Visible;
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
            try
            {
                var package = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
                {
                    var streamRef = await package.GetBitmapAsync();
                    using var stream = await streamRef.OpenReadAsync();
                    using var readStream = stream.AsStreamForRead();
                    byte[]? bytes = await ReadBoundedBytesAsync(readStream, MaxImageAttachmentBytes);
                    if (bytes is null)
                    {
                        ShowAgentInlineStatus("The pasted image exceeds the attachment limit.");
                        return;
                    }

                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    using var previewBytes = new System.IO.MemoryStream(bytes);
                    using var previewStream = previewBytes.AsRandomAccessStream();
                    await bitmap.SetSourceAsync(previewStream);

                    PendingAttachments.Add(new ImageAttachmentItem
                    {
                        FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                        ByteSize = bytes.LongLength,
                        Base64Data = Convert.ToBase64String(bytes),
                        MimeType = "image/png",
                        IsImage = true,
                        PreviewImage = bitmap,
                    });

                    AttachmentPreviewContainer.Visibility = Visibility.Visible;
                }
                else if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                {
                    var items = await package.GetStorageItemsAsync();
                    foreach (var storageItem in items)
                    {
                        if (storageItem is Windows.Storage.StorageFile file)
                        {
                            await AddFileAttachmentAsync(file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Clipboard image paste error: {ex.Message}");
            }
        }


        private async void OnAttachImageClick(object sender, RoutedEventArgs e)
        {
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
                        await AddFileAttachmentAsync(file);
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
            try
            {
                if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    foreach (var storageItem in items)
                    {
                        if (storageItem is Windows.Storage.StorageFile file)
                        {
                            await AddFileAttachmentAsync(file);
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
