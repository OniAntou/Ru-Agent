using System.Text;
using System.Text.RegularExpressions;

namespace RuBackend.Memory;

public sealed class MemoryPolicy : IMemoryPolicy
{
    private const int MaxDecisionChars = 500;
    private const int MaxEvidenceChars = 2_000;
    private const int MaxCandidateChars = 100_000;

    private static readonly Regex PathPattern = new(
        @"\b[A-Za-z]:\\[^\s,.;!?]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UsePattern = new(
        @"\b(?:sẽ dùng|dùng|uses?|will use)\b\s+(?=[^\s.,;!?])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SensitivePattern = new(
        @"\b(?:api[ _-]?key|password|mật khẩu|token|secret|bearer|authorization|credential|passphrase|access[ _-]?key|số tài khoản|bệnh án)\b|" +
        @"(?:sk|pk|ghp|gho|github_pat)_[A-Za-z0-9_-]+|" +
        @"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+|" +
        @"https?://|\[\[(?:VTS|EMO|VOICE_PACE):",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex VtsPattern = new(
        @"\[\[VTS:.*?\]\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public IReadOnlyList<MemoryCandidate> ExtractCandidates(string prompt, string response)
    {
        var normalizedPrompt = StripInternalMetadata(NormalizeText(prompt));
        var normalizedResponse = StripInternalMetadata(NormalizeText(response));
        if (IsSensitiveOrUnsafe(normalizedPrompt) || IsSensitiveOrUnsafe(normalizedResponse))
        {
            return Array.Empty<MemoryCandidate>();
        }

        var evidence = Limit(normalizedPrompt, MaxEvidenceChars);
        var candidates = new List<MemoryCandidate>();
        foreach (Match preference in PreferencePattern("anh").Matches(normalizedPrompt))
        {
            var content = preference.Groups[1].Value.Trim();
            if (content.Length > 0)
            {
                candidates.Add(new MemoryCandidate(
                    MemoryKind.Preference,
                    Limit($"Anh thích {content}.", MaxCandidateChars),
                    null,
                    evidence,
                    1.0,
                    0.8));
            }
        }

        var scopeMatch = PathPattern.Match(normalizedPrompt);
        if (scopeMatch.Success && UsePattern.IsMatch(normalizedPrompt))
        {
            var scope = CanonicalizeWindowsPath(scopeMatch.Value);
            var index = normalizedPrompt.IndexOf(scope, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var decision = normalizedPrompt[index..].Trim().TrimEnd('.');
                candidates.Add(new MemoryCandidate(
                    MemoryKind.Decision,
                    Limit(decision, MaxDecisionChars),
                    scope,
                    evidence,
                    1.0,
                    0.9));
            }
        }

        return candidates
            .GroupBy(candidate => (candidate.Kind, candidate.Content, candidate.Scope), StringTupleComparer.Instance)
            .Select(group => group.First())
            .ToArray();
    }

    public bool IsSensitiveOrUnsafe(string text) =>
        string.IsNullOrWhiteSpace(text) || SensitivePattern.IsMatch(text);

    public string StripInternalMetadata(string text) => VtsPattern.Replace(text, string.Empty).Trim();

    private static Regex PreferencePattern(string addressName) => new(
        $@"(?<!\btrai\s)(?<!\bbạn\s)\b{Regex.Escape(addressName)}\s+(?:thích|muốn|ưu tiên)\s+(.{{1,200}}?)(?:\s+và\s+(?:dự án|project)\b|[.!?]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string NormalizeText(string text) =>
        text.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static string CanonicalizeWindowsPath(string path)
    {
        // Match pathlib.PureWindowsPath's lexical handling for the forms the
        // extractor accepts: slash direction, duplicate/trailing separators,
        // and current-directory components are normalized without resolving ..
        // or changing the drive-letter case.
        var normalized = path.Replace('/', '\\');
        var drive = normalized[..2];
        var parts = normalized[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var canonicalParts = parts.Where(part => part != ".");
        var suffix = string.Join('\\', canonicalParts);
        return suffix.Length == 0 ? $"{drive}\\" : $"{drive}\\{suffix}";
    }

    private static string Limit(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    private sealed class StringTupleComparer : IEqualityComparer<(MemoryKind Kind, string Content, string? Scope)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((MemoryKind Kind, string Content, string? Scope) x, (MemoryKind Kind, string Content, string? Scope) y) =>
            x.Kind == y.Kind && string.Equals(x.Content, y.Content, StringComparison.Ordinal) && string.Equals(x.Scope, y.Scope, StringComparison.Ordinal);

        public int GetHashCode((MemoryKind Kind, string Content, string? Scope) value) =>
            HashCode.Combine(value.Kind, value.Content, value.Scope);
    }
}
