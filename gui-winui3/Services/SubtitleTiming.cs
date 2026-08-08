using System;
using System.Collections.Generic;
using System.Linq;

namespace RuApp.Services;

public sealed record TimedToken(string Token, int StartMs, int EndMs);

public static class SubtitleTiming
{
    public static IReadOnlyList<TimedToken> TokenizeWithTiming(string text, int durationMs)
    {
        var result = new List<TimedToken>();
        if (string.IsNullOrWhiteSpace(text) || durationMs <= 0) return result;

        string[] raw = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int totalChars = raw.Sum(t => t.Length);
        if (totalChars == 0) return result;

        int acc = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            int share = durationMs * raw[i].Length / totalChars;
            int start = acc; int end = acc + share;
            result.Add(new TimedToken(raw[i], start, end));
            acc = end;
        }
        // fix round-off: last token's EndMs = durationMs
        if (result.Count > 0)
        {
            var last = result[^1];
            result[^1] = last with { EndMs = durationMs };
        }
        return result;
    }
}