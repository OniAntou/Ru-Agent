using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RuApp.Models
{
    /// <summary>
    /// One chronological unit inside an assistant bubble: either a slice of
    /// streamed prose or a single agent activity row. Segments render in list
    /// order, so leading narration stays above the tool rows that followed it.
    /// </summary>
    public abstract class AssistantSegment
    {
    }

    public sealed class TextSegment : AssistantSegment, INotifyPropertyChanged
    {
        private string _text;
        private bool _isError;
        private bool _isLive;

        public TextSegment(string text)
        {
            _text = text ?? string.Empty;
        }

        public string Text
        {
            get => _text;
            set
            {
                if (!string.Equals(_text, value, StringComparison.Ordinal))
                {
                    _text = value ?? string.Empty;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasText));
                }
            }
        }

        public bool HasText => !string.IsNullOrEmpty(_text);

        /// <summary>
        /// True while this slice still streams: its MarkdownBlock parses the
        /// newest text on the view's render cadence and can play its arrival.
        /// An activity freezes and settles its preceding slice; the new tail
        /// remains live. The turn settles remaining slices (see
        /// <see cref="ChatMessageItem.SettleSegments"/>) on completion or failure.
        /// </summary>
        public bool IsLive
        {
            get => _isLive;
            set
            {
                if (_isLive != value)
                {
                    _isLive = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsError
        {
            get => _isError;
            set
            {
                if (_isError != value)
                {
                    _isError = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class ActivitySegment : AssistantSegment
    {
        public ActivitySegment(string activityId, AgentActivityItem item)
        {
            ActivityId = activityId ?? throw new ArgumentNullException(nameof(activityId));
            Item = item ?? throw new ArgumentNullException(nameof(item));
        }

        public string ActivityId { get; }

        public AgentActivityItem Item { get; }
    }

    /// <summary>
    /// Text length at the moment an activity first arrived. Persisted per
    /// message alongside its activity snapshot so reload rebuilds the same
    /// chronological order without any timestamps. Plain class (not a record):
    /// the XAML compiler cannot handle init-only positional properties.
    /// </summary>
    public sealed class ActivitySplitOffset
    {
        public ActivitySplitOffset(string activityId, int offset)
        {
            ActivityId = activityId ?? throw new ArgumentNullException(nameof(activityId));
            Offset = offset;
        }

        public string ActivityId { get; }

        public int Offset { get; }
    }

    /// <summary>
    /// Pure chronological splitter. Unknown or out-of-range offsets fall back
    /// to zero, which reproduces the legacy layout (activities above text).
    /// </summary>
    public static class ActivitySegmenter
    {
        public static IReadOnlyList<AssistantSegment> BuildSettled(
            string text,
            IReadOnlyList<AgentActivityItem> activities,
            IReadOnlyList<ActivitySplitOffset> offsets)
        {
            var safeText = text ?? string.Empty;
            var byId = new Dictionary<string, int>(StringComparer.Ordinal);
            if (offsets != null)
            {
                foreach (var split in offsets)
                {
                    if (split != null && !byId.ContainsKey(split.ActivityId))
                    {
                        byId[split.ActivityId] = Math.Max(0, split.Offset);
                    }
                }
            }

            var segments = new List<AssistantSegment>();
            var cursor = 0;
            if (activities != null)
            {
                foreach (var item in activities)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    var offset = byId.TryGetValue(item.ActivityId, out var recorded)
                        ? Math.Min(recorded, safeText.Length)
                        : 0;
                    offset = Math.Max(offset, cursor);
                    if (offset > cursor)
                    {
                        segments.Add(new TextSegment(safeText.Substring(cursor, offset - cursor)));
                        cursor = offset;
                    }

                    segments.Add(new ActivitySegment(item.ActivityId, item));
                }
            }

            if (cursor < safeText.Length)
            {
                segments.Add(new TextSegment(safeText.Substring(cursor)));
            }

            return segments;
        }
    }
}
