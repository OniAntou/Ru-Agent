using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RuBackend.Conversation;

public interface IMetadataPolicy
{
    (string VisibleText, string? Emotion) SplitReply(string rawReply);

    string StripVoiceMarkers(string text);
}

public sealed partial class MetadataPolicy : IMetadataPolicy
{
    public const string EmptyResponseText = "(Lỗi xử lý: không phản hồi được yêu cầu này.)";

    private static readonly IReadOnlySet<string> KnownEmotions = new HashSet<string>(
        ["none", "joy", "sad", "angry", "surprised", "shy"],
        StringComparer.Ordinal);

    public (string VisibleText, string? Emotion) SplitReply(string rawReply)
    {
        ArgumentNullException.ThrowIfNull(rawReply);
        var emotionMatch = EmotionMarker().Match(rawReply);
        var withoutEmotion = CollapseWhitespace(EmotionMarker().Replace(rawReply, string.Empty));
        var emotion = ResolveEmotion(emotionMatch, withoutEmotion);
        var visible = CollapseWhitespace(VtsMarker().Replace(withoutEmotion, string.Empty));
        return (visible, emotion);
    }

    public string StripVoiceMarkers(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var visible = CompletePrivateMarker().Replace(text, " ");
        visible = UnterminatedPrivateMarker().Replace(visible, string.Empty);
        visible = CollapseWhitespace(visible);
        return string.IsNullOrWhiteSpace(visible) ? EmptyResponseText : visible;
    }

    private static string ResolveEmotion(Match match, string visible)
    {
        if (match.Success)
        {
            var explicitEmotion = string.Concat(
                match.Groups[1].Value.Where(character => !char.IsWhiteSpace(character)))
                .ToLowerInvariant();
            if (KnownEmotions.Contains(explicitEmotion))
            {
                return explicitEmotion;
            }
        }

        var normalized = RemoveDiacritics(visible).ToLowerInvariant();
        if (JoyWords().IsMatch(normalized))
        {
            return "joy";
        }

        if (SadWords().IsMatch(normalized))
        {
            return "sad";
        }

        if (AngryWords().IsMatch(normalized))
        {
            return "angry";
        }

        if (SurprisedWords().IsMatch(normalized))
        {
            return "surprised";
        }

        return ShyWords().IsMatch(normalized) ? "shy" : "none";
    }

    private static string RemoveDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character is 'đ' or 'Đ' ? 'd' : character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string CollapseWhitespace(string value)
    {
        // Giữ xuống dòng cho replay: chỉ gộp khoảng trắng ngang,
        // 3+ dòng trống gộp còn tối đa 2. Gộp \s+ cũ nuốt \n\n
        // thành 1 space nên session mở lại dính thành 1 cục.
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = HorizontalWhitespace().Replace(normalized, " ");
        normalized = ExcessNewlines().Replace(normalized, "\n\n");
        var lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
        }

        return string.Join("\n", lines).Trim();
    }

    [GeneratedRegex(@"\[\[VTS:([^\]]*)\]\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VtsMarker();

    [GeneratedRegex(@"\[\[EMO:([^\[\]]*?)\]\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmotionMarker();

    [GeneratedRegex(
        @"\s*(?:\[\[(?:VTS|VOICE_PACE|EMO):[^\]]*\]\]|\[(?:SEARCH|APP_OPEN|VOLUME):[^\]]*\])\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompletePrivateMarker();

    [GeneratedRegex(
        @"\s*(?:\[\[(?:VTS|VOICE_PACE|EMO):|\[(?:SEARCH|APP_OPEN|VOLUME):).*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex UnterminatedPrivateMarker();

    [GeneratedRegex(@"[ \t\f\v\xA0]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessNewlines();

    [GeneratedRegex(@"\b(vui|mung|cuoi|ha ha|he he|phan khoi|thich lam|tuyet voi)\b", RegexOptions.CultureInvariant)]
    private static partial Regex JoyWords();

    [GeneratedRegex(@"\b(buon|buon ba|tang than|met moi|chan nan)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SadWords();

    [GeneratedRegex(@"\b(gian|tuc|buc min|nong gian)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AngryWords();

    [GeneratedRegex(@"\b(ngac nhien|giat minh|oi gioi|trui ui)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SurprisedWords();

    [GeneratedRegex(@"\b(xau ho|ngai ngu|then tho)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ShyWords();
}
