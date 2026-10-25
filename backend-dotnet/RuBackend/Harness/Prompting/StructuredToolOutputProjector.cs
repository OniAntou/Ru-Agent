using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuBackend.Harness.ModelRunner;
using RuAgent.Runtime.Model;

namespace RuBackend.Harness.Prompting;

internal enum ToolOutputProjectionUnit
{
    Utf8Bytes,
    Utf16Characters,
}

internal static class StructuredToolOutputProjector
{
    private const int MaximumKnownOutputChars = 4 * 1024 * 1024;
    private const string ProjectionErrorCode = "tool_output_projection_invalid";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Project(
        string toolId,
        JsonElement? arguments,
        string content,
        int maximum,
        ToolOutputProjectionUnit unit,
        Func<string, int, string> fitUnknown,
        string? toolCallId = null,
        string? providerCallId = null)
    {
        ArgumentNullException.ThrowIfNull(toolId);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fitUnknown);

        // Trace is durable completion/audit evidence, not model context. Always
        // remove it, including when the unprojected response already fits.
        if (toolId == "run_code")
        {
            try
            {
                var program = JsonNode.Parse(content) as JsonObject ?? throw ProjectionFailure();
                var visible = new JsonObject();
                foreach (var name in new[] { "program_id", "program_revision", "status", "error", "result", "tool_count", "effects_unknown", "blocked_call" })
                    if (program.TryGetPropertyValue(name, out var value)) visible[name] = value?.DeepClone();
                content = visible.ToJsonString();
            }
            catch (JsonException) { throw ProjectionFailure(); }
        }

        var fits = Measure(content, unit) <= maximum;
        var parsed = TryParseKnown(toolId, arguments, content, toolCallId, providerCallId, requiresProjection: !fits);
        if (parsed is null)
            return fits ? content : fitUnknown(content, maximum);

        if (!parsed.SourceHasMore && fits)
            return content;

        return FitStructured(parsed, maximum, unit);
    }

    private static ProjectionDocument? TryParseKnown(
        string toolId,
        JsonElement? arguments,
        string content,
        string? toolCallId,
        string? providerCallId,
        bool requiresProjection)
    {
        if (content.Length == 0 || content.Length > MaximumKnownOutputChars)
            return null;

        try
        {
            var root = JsonNode.Parse(content) as JsonObject;
            if (root is null)
                return null;

            if (toolId == "read_file" && root["content"] is JsonValue lineContent
                && lineContent.TryGetValue<string>(out var lineText))
            {
                if (!ReadNonnegativeInteger(root, "start_line", out var firstLine) || firstLine < 1)
                    throw ProjectionFailure();
                return ProjectionDocument.ForLines(root, lineText, firstLine, toolCallId, providerCallId);
            }

            if (toolId is "read" or "files.read" && root["content"] is JsonValue readContent
                && readContent.TryGetValue<string>(out var readText))
            {
                if (!ReadNonnegativeInteger(root, "offset", out var readOffset))
                    throw ProjectionFailure();
                return ProjectionDocument.ForText(root, "content", readText, readOffset, cursorInBytes: true, toolCallId, providerCallId);
            }

            if (toolId is "grep" or "files.grep" && root["results"] is JsonArray grepResults)
            {
                if (!ReadOriginalOffset(arguments, out var grepOffset))
                    throw ProjectionFailure();
                return ProjectionDocument.ForArray(root, "results", grepResults, grepOffset, toolCallId, providerCallId);
            }

            if (toolId == "web_fetch" && root.ContainsKey("offset")
                && root["content"] is JsonValue webContent && webContent.TryGetValue<string>(out var webText))
            {
                if (!ReadNonnegativeInteger(root, "offset", out var webOffset))
                    throw ProjectionFailure();
                const string contentMarker = "\ncontent: ";
                const string suffix = "\n[/untrusted_web_reference]";
                const string truncatedMarker = "...[truncated]";
                var start = webText.IndexOf(contentMarker, StringComparison.Ordinal);
                if (start < 0 || !webText.StartsWith("[untrusted_web_reference]\n", StringComparison.Ordinal)
                    || !webText.EndsWith(suffix, StringComparison.Ordinal))
                    throw ProjectionFailure();
                start += contentMarker.Length;
                var body = webText[start..^suffix.Length];
                if (HasMore(root) && body.EndsWith(truncatedMarker, StringComparison.Ordinal))
                    body = body[..^truncatedMarker.Length];
                return ProjectionDocument.ForWrappedText(root, body, webOffset,
                    webText[..start], suffix, toolCallId, providerCallId);
            }

            if (toolId.Length == 0 && root["results"] is JsonArray && root["count"] is JsonValue
                && (HasMore(root) || requiresProjection))
            {
                throw ProjectionFailure();
            }

            if (toolId.Length == 0 && root["content"] is JsonValue && root["path"] is JsonValue
                && root["offset"] is JsonValue && (HasMore(root) || requiresProjection))
            {
                throw ProjectionFailure();
            }

            if (toolId.Length == 0 && root["page"] is JsonValue && root["spool_id"] is JsonValue
                && root["offset"] is JsonValue && (HasMore(root) || requiresProjection))
            {
                throw ProjectionFailure();
            }

            if (toolId == "terminal_read"
                && root["page"] is JsonValue outputPage
                && outputPage.TryGetValue<string>(out var page)
                && root["spool_id"] is JsonValue spoolId
                && spoolId.TryGetValue<string>(out _))
            {
                if (!ReadNonnegativeInteger(root, "offset", out var outputOffset))
                    throw ProjectionFailure();
                return ProjectionDocument.ForText(root, "page", page, outputOffset, cursorInBytes: false, toolCallId, providerCallId);
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    private static string FitStructured(ProjectionDocument document, int maximum, ToolOutputProjectionUnit unit)
    {
        if (maximum <= 0)
            throw ProjectionFailure();

        var full = document.Serialize(document.FullVisibleLength);
        if (Measure(full, unit) <= maximum)
            return full;

        var low = 0;
        var high = document.FullVisibleLength;
        string? best = null;
        var bestCount = 0;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var candidate = document.Serialize(middle);
            if (Measure(candidate, unit) <= maximum)
            {
                best = candidate;
                bestCount = document.VisibleCount(middle);
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (best is null || bestCount == 0)
            throw ProjectionFailure();
        return best;
    }

    private static int Measure(string value, ToolOutputProjectionUnit unit) => unit switch
    {
        ToolOutputProjectionUnit.Utf8Bytes => StrictUtf8.GetByteCount(value),
        ToolOutputProjectionUnit.Utf16Characters => value.Length,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    private static bool ReadOriginalOffset(JsonElement? arguments, out int offset)
    {
        offset = 0;
        if (arguments is not { ValueKind: JsonValueKind.Object } value)
            return false;
        if (!value.TryGetProperty("offset", out var property))
            return true;
        return property.TryGetInt32(out offset) && offset >= 0;
    }

    private static bool ReadNonnegativeInteger(JsonObject root, string name, out int value)
    {
        value = 0;
        return root[name] is JsonValue property
            && property.TryGetValue<int>(out value)
            && value >= 0;
    }

    private static bool HasMore(JsonObject root) =>
        root["truncated"] is JsonValue value && value.TryGetValue<bool>(out var truncated) && truncated;

    private static ModelRunnerProtocolException ProjectionFailure() => new(ProjectionErrorCode, retryable: false);

    private sealed class ProjectionDocument
    {
        private readonly JsonObject _root;
        private readonly string _field;
        private readonly string? _text;
        private readonly JsonArray? _items;
        private readonly int _sourceOffset;
        private readonly bool _cursorInBytes;
        private readonly bool _cursorInLines;
        private readonly string? _toolCallId;
        private readonly string? _providerCallId;
        private readonly string _prefix = "";
        private readonly string _suffix = "";

        private ProjectionDocument(
            JsonObject root,
            string field,
            string text,
            int sourceOffset,
            bool cursorInBytes,
            string? toolCallId,
            string? providerCallId,
            string prefix = "",
            string suffix = "",
            bool cursorInLines = false)
        {
            _root = root;
            _field = field;
            _text = text;
            _sourceOffset = sourceOffset;
            _cursorInBytes = cursorInBytes;
            _cursorInLines = cursorInLines;
            _toolCallId = toolCallId;
            _providerCallId = providerCallId;
            _prefix = prefix;
            _suffix = suffix;
            FullVisibleLength = text.Length;
        }

        private ProjectionDocument(
            JsonObject root,
            string field,
            JsonArray items,
            int sourceOffset,
            string? toolCallId,
            string? providerCallId)
        {
            _root = root;
            _field = field;
            _items = items;
            _sourceOffset = sourceOffset;
            _toolCallId = toolCallId;
            _providerCallId = providerCallId;
            FullVisibleLength = items.Count;
        }

        internal int FullVisibleLength { get; }

        internal bool SourceHasMore => ReadSourceTruncated(_root);

        internal static ProjectionDocument ForText(
            JsonObject root,
            string field,
            string text,
            int offset,
            bool cursorInBytes,
            string? toolCallId,
            string? providerCallId) =>
            new(root, field, text, offset, cursorInBytes, toolCallId, providerCallId);

        internal static ProjectionDocument ForArray(
            JsonObject root,
            string field,
            JsonArray items,
            int offset,
            string? toolCallId,
            string? providerCallId) =>
            new(root, field, items, offset, toolCallId, providerCallId);

        internal static ProjectionDocument ForWrappedText(JsonObject root, string text, int offset,
            string prefix, string suffix, string? toolCallId, string? providerCallId) =>
            new(root, "content", text, offset, false, toolCallId, providerCallId, prefix, suffix);

        internal static ProjectionDocument ForLines(JsonObject root, string text, int firstLine,
            string? toolCallId, string? providerCallId) =>
            new(root, "content", text, firstLine, false, toolCallId, providerCallId, cursorInLines: true);

        internal string Serialize(int visibleLength)
        {
            var projected = (JsonObject)_root.DeepClone();
            if (_text is not null)
            {
                var length = PrefixLength(_text, visibleLength);
                var visible = _text[..length];
                var advance = _cursorInLines ? visible.Count(character => character == '\n')
                    + (visible.Length > 0 && !visible.EndsWith('\n') ? 1 : 0)
                    : _cursorInBytes ? StrictUtf8.GetByteCount(visible) : visible.Length;
                var hasMore = length < _text.Length || ReadSourceTruncated(projected);
                projected[_field] = _prefix.Length == 0 ? visible
                    : _prefix + visible + (hasMore ? "...[truncated]" : "") + _suffix;
                if (_cursorInLines)
                {
                    projected["next_line"] = hasMore ? checked(_sourceOffset + advance) : null;
                    projected["has_more"] = hasMore;
                    projected["line_count"] = advance;
                    projected["projection_next_line"] = hasMore ? checked(_sourceOffset + advance) : null;
                }
                else projected["projection_next_offset"] = checked(_sourceOffset + advance);
                projected["projection_visible_characters"] = length;
                if (_cursorInBytes)
                    projected["projection_visible_bytes"] = advance;
                projected["projection_has_more"] = hasMore;
            }
            else
            {
                var visibleCount = Math.Clamp(visibleLength, 0, _items!.Count);
                var visible = new JsonArray();
                for (var index = 0; index < visibleCount; index++)
                    visible.Add(_items[index]?.DeepClone());
                projected[_field] = visible;
                projected["projection_next_offset"] = checked(_sourceOffset + visibleCount);
                projected["projection_visible_results"] = visibleCount;
                projected["projection_has_more"] = visibleCount < _items.Count || ReadSourceTruncated(projected);
            }

            if (_toolCallId is not null)
                projected["projection_tool_call_id"] = _toolCallId;
            if (_providerCallId is not null)
                projected["projection_provider_call_id"] = _providerCallId;

            return projected.ToJsonString();
        }

        internal int VisibleCount(int visibleLength) => _text is null
            ? Math.Clamp(visibleLength, 0, _items!.Count)
            : PrefixLength(_text, visibleLength);

        private int PrefixLength(string text, int desiredLength)
        {
            var length = SafePrefixLength(text, desiredLength);
            if (!_cursorInLines || length == text.Length || length == 0) return length;
            return text.LastIndexOf('\n', length - 1, length) + 1;
        }

        private static int SafePrefixLength(string text, int desiredLength)
        {
            var length = Math.Clamp(desiredLength, 0, text.Length);
            if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
                length--;
            return length;
        }

        private static bool ReadSourceTruncated(JsonObject root)
        {
            if (root["has_more"] is JsonValue more && more.TryGetValue<bool>(out var hasMore)) return hasMore;
            // Modern grep distinguishes an incomplete scan from a known next page.
            // Preserve legacy receipts that only carry truncated, while never turning
            // a timeout/scan cap with next_offset=null into an endless paging cursor.
            if (root.ContainsKey("next_offset"))
                return ReadNonnegativeInteger(root, "next_offset", out _);
            return root["truncated"] is JsonValue value && value.TryGetValue<bool>(out var truncated) && truncated;
        }
    }
}
