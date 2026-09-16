using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RuApp.Models
{
    public readonly record struct RectPx(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    public class ChatMessageItem : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        private string _role = "user";
        private string _content = string.Empty;
        private DateTime _timestamp = DateTime.Now;
        private bool _isStreaming = false;
        private bool _isError = false;
        private string? _turnId;
        private string? _sessionId;
        private AgentModelUsage? _usage;
        private ObservableCollection<AgentActivityItem>? _agentActivities;

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string Role
        {
            get => _role;
            set
            {
                if (SetProperty(ref _role, value))
                {
                    OnPropertyChanged(nameof(RoleLabel));
                    OnPropertyChanged(nameof(ShowTypingDots));
                    OnPropertyChanged(nameof(ShowContent));
                }
            }
        }

        public string Content
        {
            get => _content;
            set
            {
                if (SetProperty(ref _content, value))
                {
                    OnPropertyChanged(nameof(ShowTypingDots));
                    OnPropertyChanged(nameof(ShowContent));
                }
            }
        }

        public DateTime Timestamp
        {
            get => _timestamp;
            set
            {
                if (SetProperty(ref _timestamp, value))
                {
                    OnPropertyChanged(nameof(TimeLabel));
                }
            }
        }

        public bool IsStreaming
        {
            get => _isStreaming;
            set
            {
                if (SetProperty(ref _isStreaming, value))
                {
                    OnPropertyChanged(nameof(ShowTypingDots));
                    OnPropertyChanged(nameof(ShowContent));
                }
            }
        }

        public bool IsError
        {
            get => _isError;
            set => SetProperty(ref _isError, value);
        }

        public string? TurnId
        {
            get => _turnId;
            set => SetProperty(ref _turnId, value);
        }

        public string? SessionId
        {
            get => _sessionId;
            set => SetProperty(ref _sessionId, value);
        }

        public AgentModelUsage? Usage
        {
            get => _usage;
            set
            {
                if (SetProperty(ref _usage, value))
                {
                    OnPropertyChanged(nameof(HasUsage));
                    OnPropertyChanged(nameof(UsageLabel));
                }
            }
        }

        public bool HasUsage => Usage?.HasAny == true;

        public string UsageLabel => Usage?.DisplayText ?? string.Empty;

        public ObservableCollection<AgentActivityItem>? AgentActivities
        {
            get => _agentActivities;
            set
            {
                if (ReferenceEquals(_agentActivities, value))
                {
                    return;
                }

                if (_agentActivities is not null)
                {
                    _agentActivities.CollectionChanged -= OnAgentActivitiesChanged;
                }

                _agentActivities = value;
                if (_agentActivities is not null)
                {
                    _agentActivities.CollectionChanged += OnAgentActivitiesChanged;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAgentActivities));
                OnPropertyChanged(nameof(ShowLegacyActivityFeed));
            }
        }

        public bool HasAgentActivities => AgentActivities?.Count > 0;

        private ObservableCollection<AssistantSegment>? _segments;

        /// <summary>
        /// Chronological render order: frozen prose slices interleaved with the
        /// activity rows that followed them, plus one live tail TextSegment that
        /// keeps growing while streaming. Null means legacy layout (feed above
        /// the whole text). Rebuilt from Content + AgentActivities + offsets.
        /// </summary>
        public ObservableCollection<AssistantSegment>? Segments
        {
            get => _segments;
            set
            {
                if (ReferenceEquals(_segments, value))
                {
                    return;
                }

                if (_segments is not null)
                {
                    _segments.CollectionChanged -= OnSegmentsChanged;
                }

                _segments = value;
                if (_segments is not null)
                {
                    _segments.CollectionChanged += OnSegmentsChanged;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSegments));
                OnPropertyChanged(nameof(ShowLegacyActivityFeed));
                OnPropertyChanged(nameof(ShowLegacyText));
            }
        }

        /// <summary>
        /// <see cref="HasSegments"/> is derived from the collection's count, so it has to
        /// follow the collection and not only the reference. The live turn creates the
        /// collection empty and then adds to it, so a notification raised only by the
        /// setter left <see cref="HasSegments"/> stuck false for the whole turn: the
        /// segments control stayed collapsed and the legacy feed stayed on screen, which
        /// is what made the transcript appear to empty itself mid-turn and fill in again
        /// when the reload rebuilt the collection in one assignment.
        /// </summary>
        private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasSegments));
            OnPropertyChanged(nameof(ShowLegacyActivityFeed));
            OnPropertyChanged(nameof(ShowLegacyText));
        }

        public bool HasSegments => Segments?.Count > 0;

        /// <summary>Legacy feed: visible only when no chronological segments exist.</summary>
        public bool ShowLegacyActivityFeed => HasAgentActivities && !HasSegments;

        /// <summary>Legacy full-text block: hidden once segments take over rendering.</summary>
        public bool ShowLegacyText => !HasSegments;

        private List<ActivitySplitOffset>? _activitySplitOffsets;

        /// <summary>
        /// Text length at each activity arrival, parallel to the activity
        /// snapshot. Copied wherever AgentActivities is snapshotted so reload
        /// rebuilds the same chronological order.
        /// </summary>
        public List<ActivitySplitOffset>? ActivitySplitOffsets
        {
            get => _activitySplitOffsets;
            set
            {
                if (ReferenceEquals(_activitySplitOffsets, value))
                {
                    return;
                }

                _activitySplitOffsets = value;
                OnPropertyChanged();
            }
        }

        private System.Collections.ObjectModel.ObservableCollection<ImageAttachmentItem> _attachments = new();

        public System.Collections.ObjectModel.ObservableCollection<ImageAttachmentItem> Attachments
        {
            get => _attachments;
            set
            {
                if (SetProperty(ref _attachments, value))
                {
                    OnPropertyChanged(nameof(HasAttachments));
                }
            }
        }

        public bool HasAttachments => Attachments != null && Attachments.Count > 0;

        public bool ShowTypingDots => Role == "assistant" && IsStreaming && string.IsNullOrEmpty(Content);

        public bool ShowContent => !ShowTypingDots;

        public string RoleLabel => Role == "assistant" ? "Ru" : "You";

        public string TimeLabel => Timestamp.ToString("HH:mm");

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnAgentActivitiesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasAgentActivities));
            OnPropertyChanged(nameof(ShowLegacyActivityFeed));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ImageAttachmentItem : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _fileName = string.Empty;
        private string? _filePath;
        private string? _base64Data;
        private string? _textContent;
        private bool _isImage = true;
        private string _mimeType = "image/png";
        private long _byteSize;
        private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _previewImage;

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        public string? FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        public string? Base64Data
        {
            get => _base64Data;
            set
            {
                if (SetProperty(ref _base64Data, value))
                {
                    EnsurePreviewFromBase64();
                }
            }
        }

        public void EnsurePreviewFromBase64()
        {
            if (_previewImage != null || string.IsNullOrWhiteSpace(_base64Data) || !_isImage)
            {
                return;
            }

            byte[] bytes;
            try
            {
                var raw = _base64Data;
                var commaIdx = raw.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
                if (commaIdx >= 0)
                {
                    raw = raw.Substring(commaIdx + 8);
                }

                bytes = Convert.FromBase64String(raw);
                if (_byteSize == 0)
                {
                    ByteSize = bytes.Length;
                }
            }
            catch
            {
                // Fallback gracefully if decoding fails
                return;
            }

            // Bitmap decode + SetSource must not block the UI thread; the
            // continuation returns to the captured context before touching
            // the BitmapImage.
            _ = LoadPreviewImageAsync(bytes);
        }

        public async Task LoadPreviewImageAsync(byte[] bytes)
        {
            try
            {
                var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }
                stream.Seek(0);
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bmp.SetSource(stream);
                _previewImage = bmp;
                _thumbnailImage = bmp;
                OnPropertyChanged(nameof(PreviewImage));
                OnPropertyChanged(nameof(ThumbnailImage));
            }
            catch
            {
                // Fallback gracefully if decoding fails
            }
        }

        public string? TextContent
        {
            get => _textContent;
            set => SetProperty(ref _textContent, value);
        }

        public bool IsImage
        {
            get => _isImage;
            set
            {
                if (SetProperty(ref _isImage, value))
                {
                    OnPropertyChanged(nameof(IsDocument));
                    OnPropertyChanged(nameof(FileIconGlyph));
                }
            }
        }

        public bool IsDocument => !IsImage;

        public string FileExtension => System.IO.Path.GetExtension(FileName).ToLowerInvariant();

        public bool IsCodeFile => FileExtension switch
        {
            ".cs" or ".py" or ".js" or ".ts" or ".jsx" or ".tsx" or ".json" or ".xml" or ".yaml" or ".yml"
            or ".html" or ".css" or ".scss" or ".sql" or ".sh" or ".bat" or ".ps1" or ".rs" or ".go"
            or ".cpp" or ".c" or ".h" or ".hpp" or ".java" or ".kt" or ".swift" or ".dart" => true,
            _ => false
        };

        public string FileIconGlyph => IsImage ? "\uEB9F" : (IsCodeFile ? "\uE943" : "\uE8A5");

        public string MimeType
        {
            get => _mimeType;
            set => SetProperty(ref _mimeType, value);
        }

        public long ByteSize
        {
            get => _byteSize;
            set
            {
                if (SetProperty(ref _byteSize, value))
                {
                    OnPropertyChanged(nameof(FormattedSize));
                }
            }
        }

        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? PreviewImage
        {
            get => _previewImage;
            set => SetProperty(ref _previewImage, value);
        }

        private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _thumbnailImage;

        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? ThumbnailImage
        {
            get => _thumbnailImage ?? _previewImage;
            set => SetProperty(ref _thumbnailImage, value);
        }

        public string FormattedSize
        {
            get
            {
                if (ByteSize < 1024) return $"{ByteSize} B";
                if (ByteSize < 1024 * 1024) return $"{ByteSize / 1024.0:F1} KB";
                return $"{ByteSize / (1024.0 * 1024.0):F1} MB";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
