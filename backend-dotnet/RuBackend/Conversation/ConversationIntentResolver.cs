namespace RuBackend.Conversation;

public sealed class ConversationIntentResolver : IConversationIntentResolver
{
    private static readonly HashSet<string> Greetings = new(StringComparer.Ordinal)
    {
        "xin chao",
        "xin chao em",
        "chao em",
        "chao anh",
        "em oi",
        "hello",
        "hello ru",
        "hi",
        "hey",
    };

    private static readonly HashSet<string> Acknowledgements = new(StringComparer.Ordinal)
    {
        "ok",
        "oke",
        "okay",
        "duoc",
        "duoc roi",
        "cam on",
        "cam on em",
        "thanks",
        "thank you",
        "duyet",
        "roi",
        "uh",
        "um",
        "vang",
        "vâng",
    };

    public ConversationIntentResolution Resolve(string prompt)
    {
        var raw = (prompt ?? string.Empty).Trim();
        var normalized = Normalize(raw);
        if (Greetings.Contains(normalized))
        {
            return new ConversationIntentResolution(
                ConversationIntentKind.Greeting,
                0.99,
                "Chào anh. Em đang nghe đây.");
        }

        if (Acknowledgements.Contains(normalized))
        {
            return new ConversationIntentResolution(
                ConversationIntentKind.Acknowledgement,
                0.98,
                "Vâng, em nghe đây.");
        }

        if (IsCorrection(normalized))
        {
            return new ConversationIntentResolution(
                ConversationIntentKind.Correction,
                0.93,
                ModelInstruction: "Đây là câu sửa hoặc làm rõ ý trước đó; giữ toàn bộ ngữ cảnh gần nhất và trả lời đúng ý mới nhất.");
        }

        if (IsPathTask(raw, normalized))
        {
            return new ConversationIntentResolution(ConversationIntentKind.PathTask, 0.92);
        }

        if (ContainsAny(normalized, "tim kiem", "tim", "search", "tra cuu", "look up"))
        {
            return new ConversationIntentResolution(ConversationIntentKind.Search, 0.9);
        }

        if (ContainsAny(
                normalized,
                "hom nay",
                "bay gio",
                "mấy gio",
                "may gio",
                "thoi tiet",
                "trang thai",
                "hien tai",
                "current",
                "latest",
                "pin"))
        {
            return new ConversationIntentResolution(ConversationIntentKind.CurrentFact, 0.9);
        }

        return new ConversationIntentResolution(ConversationIntentKind.NoMatch, 0);
    }

    private static bool IsCorrection(string normalized) =>
        normalized.StartsWith("khong ", StringComparison.Ordinal)
        || normalized.StartsWith("y anh la", StringComparison.Ordinal)
        || normalized.StartsWith("y em la", StringComparison.Ordinal)
        || normalized.StartsWith("sua lai", StringComparison.Ordinal)
        || normalized.StartsWith("khong phai", StringComparison.Ordinal)
        || normalized.StartsWith("dinh chinh", StringComparison.Ordinal);

    private static bool IsPathTask(string raw, string normalized) =>
        normalized.Contains("folder", StringComparison.Ordinal)
        || normalized.Contains("thu muc", StringComparison.Ordinal)
        || normalized.Contains("workspace", StringComparison.Ordinal)
        || normalized.Contains("truy cap", StringComparison.Ordinal)
        || normalized.Contains("duong dan", StringComparison.Ordinal)
        || normalized.Contains("mo file", StringComparison.Ordinal)
        || normalized.Contains("doc file", StringComparison.Ordinal)
        || raw.Contains(":\\", StringComparison.Ordinal)
        || raw.Contains("/", StringComparison.Ordinal);

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(value.Contains);

    private static string Normalize(string value)
    {
        var lowered = value.ToLowerInvariant().Replace('đ', 'd');
        var folded = lowered.Normalize(System.Text.NormalizationForm.FormD);
        var chars = folded
            .Where(character =>
                System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ? character : ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
    }
}
