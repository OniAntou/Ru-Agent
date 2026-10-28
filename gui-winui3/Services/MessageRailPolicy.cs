using System;
using System.Text;
using System.Text.RegularExpressions;

namespace RuApp.Services
{
    /// <summary>What a rail tick shows on hover: a one-line title and a short plain-text body.</summary>
    public readonly record struct MessageRailPreview(string Title, string Body);

    /// <summary>
    /// Geometry and text for the transcript's message rail. The transcript
    /// virtualizes, so unrealized rows have no trustworthy pixel position:
    /// ticks are spaced by message index, never by scroll geometry. A
    /// transcript too long for one tick per message groups neighbours into
    /// one tick, so the rail never grows past its host.
    /// </summary>
    public static partial class MessageRailPolicy
    {
        public const double MaxPitch = 10;
        public const double MinPitch = 4;
        public const double RailWidth = 28;
        public const int TitleMaxLength = 120;
        public const int BodyMaxLength = 280;

        /// <summary>Ticks drawn for <paramref name="messageCount"/> messages in <paramref name="height"/> pixels.</summary>
        public static int TickCount(int messageCount, double height)
        {
            if (messageCount <= 0 || !double.IsFinite(height) || height <= 0) return 0;
            var fit = (int)Math.Floor(height / MinPitch);
            return Math.Max(1, Math.Min(messageCount, fit));
        }

        public static double Pitch(int tickCount, double height)
        {
            if (tickCount <= 0 || !double.IsFinite(height) || height <= 0) return MaxPitch;
            return Math.Min(MaxPitch, height / tickCount);
        }

        /// <summary>The first message a tick stands for; tick t covers [first(t), first(t + 1)).</summary>
        public static int FirstMessageOfTick(int tick, int tickCount, int messageCount)
        {
            if (tickCount <= 0 || messageCount <= 0) return -1;
            tick = Math.Clamp(tick, 0, tickCount - 1);
            return (int)((long)tick * messageCount / tickCount);
        }

        /// <summary>The tick whose range contains <paramref name="message"/>; inverse of <see cref="FirstMessageOfTick"/>.</summary>
        public static int TickOfMessage(int message, int tickCount, int messageCount)
        {
            if (tickCount <= 0 || messageCount <= 0) return -1;
            message = Math.Clamp(message, 0, messageCount - 1);
            return (int)(((long)(message + 1) * tickCount - 1) / messageCount);
        }

        /// <summary>The tick nearest a pointer at <paramref name="y"/> on a rail whose ticks start at zero.</summary>
        public static int TickAt(double y, int tickCount, double pitch)
        {
            if (tickCount <= 0 || !double.IsFinite(y) || pitch <= 0) return -1;
            return Math.Clamp((int)Math.Floor(y / pitch), 0, tickCount - 1);
        }

        /// <summary>
        /// How far a tick sticks out. At rest every tick is the same length and
        /// the one in view is a little longer; hovering lifts the tick under the
        /// pointer most and tapers over its two neighbours on each side.
        /// </summary>
        public static double TickScale(int tick, int hoverTick, int currentTick)
        {
            var lift = hoverTick < 0 ? 1.0 : Math.Abs(tick - hoverTick) switch
            {
                0 => 2.5,
                1 => 1.75,
                2 => 1.3,
                _ => 1.0,
            };
            return tick == currentTick ? Math.Max(lift, 1.25) : lift;
        }

        /// <summary>
        /// The rail lives in the transcript's left gutter. It shows only while
        /// that gutter is wide enough to keep its ticks off the reading column.
        /// </summary>
        public static bool HasRoom(double hostWidth, double readingColumnMaxWidth, int messageCount)
            => messageCount >= 2 && double.IsFinite(hostWidth)
               && hostWidth >= readingColumnMaxWidth + 2 * (RailWidth + 12);

        /// <summary>Markdown-free title (first non-empty line) and body (the rest, flattened).</summary>
        public static MessageRailPreview Preview(string? content, bool isUser)
        {
            var lines = (content ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            string? title = null;
            var body = new StringBuilder();
            var inFence = false;
            foreach (var raw in lines)
            {
                if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }

                var line = inFence ? raw.Trim() : StripMarkdown(raw);
                if (line.Length == 0) continue;
                if (title is null)
                {
                    title = line;
                    continue;
                }

                if (body.Length > 0) body.Append(' ');
                body.Append(line);
                if (body.Length > BodyMaxLength) break;
            }

            return new MessageRailPreview(
                Truncate(title ?? (isUser ? "Message" : "Working…"), TitleMaxLength),
                Truncate(body.ToString(), BodyMaxLength));
        }

        private static string StripMarkdown(string line)
        {
            var text = LeadingMarkerPattern().Replace(line.Trim(), string.Empty);
            text = ImagePattern().Replace(text, "$1");
            text = LinkPattern().Replace(text, "$1");
            text = EmphasisPattern().Replace(text, string.Empty);
            text = TableEdgePattern().Replace(text, " ");
            return WhitespacePattern().Replace(text, " ").Trim();
        }

        private static string Truncate(string text, int maxLength)
            => text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";

        // Headings, quotes, bullets, numbered items and task boxes at the line start.
        [GeneratedRegex(@"^(?:#{1,6}\s+|>\s*|[-*+]\s+(?:\[[ xX]\]\s+)?|\d+[.)]\s+)+")]
        private static partial Regex LeadingMarkerPattern();

        [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
        private static partial Regex ImagePattern();

        [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
        private static partial Regex LinkPattern();

        [GeneratedRegex(@"\*\*|__|~~|`|(?<!\w)[*_](?=\S)|(?<=\S)[*_](?!\w)")]
        private static partial Regex EmphasisPattern();

        [GeneratedRegex(@"\s*\|\s*")]
        private static partial Regex TableEdgePattern();

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespacePattern();
    }
}
