using System;

namespace RuApp.Models
{
    public static class ModelContextSpecs
    {
        public static (int MaxTokens, string DisplayText) GetContextSpec(string? modelName, int? catalogContextLength = null)
        {
            if (catalogContextLength is > 0)
            {
                return (catalogContextLength.Value, FormatTokens(catalogContextLength.Value));
            }

            if (string.IsNullOrWhiteSpace(modelName))
                return (131_072, "128K");

            string lower = modelName.ToLowerInvariant();

            var match = System.Text.RegularExpressions.Regex.Match(lower, @"(?i)(?:^|[-_/:])(\d+)(k|m)(?:[-_:]|$)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var num) && num > 0)
            {
                string unit = match.Groups[2].Value.ToLowerInvariant();
                int tokens = unit == "m" ? num * 1_000_000 : num * 1_024;
                return (tokens, $"{num}{unit.ToUpperInvariant()}");
            }

            if (lower.Contains("gemini-1.5-pro") || lower.Contains("gemini-2.5-pro") || lower.Contains("gemini-2.0-pro"))
                return (2_000_000, "2M");

            if (lower.Contains("gemini") || lower.Contains("flash"))
                return (1_000_000, "1M");

            if (lower.Contains("claude-3-7") || lower.Contains("claude-3-5") || lower.Contains("claude-3") ||
                lower.Contains("o1") || lower.Contains("o3") || lower.Contains("o4"))
                return (200_000, "200K");

            if (lower.Contains("llama-3.3") || lower.Contains("llama-3.1") ||
                lower.Contains("qwen2.5") || lower.Contains("qwen-2.5") || lower.Contains("dots") ||
                lower.Contains("gpt-4o") || lower.Contains("gpt-4-turbo") || lower.Contains("codestral"))
                return (131_072, "128K");

            if (lower.Contains("deepseek"))
                return (65_536, "64K");

            if (lower.Contains("mistral") || lower.Contains("mixtral") ||
                lower.Contains("phi-3") || lower.Contains("phi-4") || lower.Contains("gemma-2"))
                return (32_768, "32K");

            if (lower.Contains("gpt-3.5-turbo-16k"))
                return (16_384, "16K");

            if (lower.Contains("llama-2") || lower.Contains("llama-3-8b"))
                return (8_192, "8K");

            if (lower.Contains("gpt-3.5-turbo"))
                return (4_096, "4K");

            return (131_072, "128K");
        }

        /// <summary>Rough characters-per-token ratio used for the context meter.</summary>
        private const double CharsPerToken = 3.5;

        /// <summary>
        /// Flat token charge per attached image. An estimate, not a measurement: the
        /// meter exists to warn before a request is rejected, not to bill exactly.
        /// </summary>
        private const int TokensPerImage = RuAgent.Protocol.Harness.ContextTokenBreakdown.ImageTokenAllowance;

        public static int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return Math.Max(1, (int)Math.Ceiling(text.Length / CharsPerToken));
        }

        public static int EstimateTokens(int charCount, int imageCount = 0)
        {
            if (charCount <= 0 && imageCount <= 0) return 0;
            int textTokens = charCount > 0 ? Math.Max(1, (int)Math.Ceiling(charCount / CharsPerToken)) : 0;
            return textTokens + (imageCount * TokensPerImage);
        }

        public static string FormatTokens(int tokens)
        {
            return RuAgent.SessionInterchange.TranscriptText.FormatTokens(tokens);
        }

        /// <summary>
        /// A context figure the provider actually published, or null when it published
        /// none.
        /// </summary>
        /// <remarks>
        /// Deliberately not <see cref="GetContextSpec"/>. That one guesses: it matches
        /// substrings of the model name and, failing every branch, returns 128K for a
        /// model it knows nothing about. A guess is right often enough to be
        /// useless — it is wrong silently, and a caller has no way to tell a published
        /// figure from a substring match on the vendor's name.
        ///
        /// The context meter is allowed the guess, because it exists to warn before a
        /// request is rejected and says so. A picker lists models next to each other,
        /// so a wrong number there is a wrong number someone will choose a model by.
        /// Null is the honest answer and the caller shows nothing.
        /// </remarks>
        public static string? FormatPublishedContext(int? publishedTokens)
        {
            return publishedTokens is > 0 ? FormatTokens(publishedTokens.Value) : null;
        }
    }
}
