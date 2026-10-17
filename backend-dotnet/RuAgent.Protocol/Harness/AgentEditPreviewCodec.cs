using System.Text.Json;

namespace RuAgent.Protocol.Harness;

/// <summary>Bounded display metadata; legacy previews remain readable on replay.</summary>
public static class AgentEditPreviewCodec
{
    public const int MaximumLength = 4_000;
    private const string Prefix = "@@json ";

    public static string? Encode(string before, string after)
    {
        if (before.Concat(after).Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t')) return null;
        int available = MaximumLength - Prefix.Length - 3;
        int beforeBudget = before.Length == 0 ? 2 : after.Length == 0 ? available - 2 : available / 2;
        return Prefix + "[" + Bound(before, beforeBudget) + "," + Bound(after, available - beforeBudget) + "]";
    }

    private static string Bound(string text, int budget)
    {
        int low = 0, high = Math.Min(text.Length, MaximumLength);
        while (low < high)
        {
            int middle = low + (high - low + 1) / 2;
            if (JsonSerializer.Serialize(text[..middle]).Length <= budget) low = middle;
            else high = middle - 1;
        }
        if (low > 0 && low < text.Length && char.IsHighSurrogate(text[low - 1])) low--;
        return JsonSerializer.Serialize(text[..low]);
    }

    public static bool TryDecode(string? detail, out string before, out string after)
    {
        before = after = string.Empty;
        if (detail is null || detail.Length > MaximumLength) return false;
        if (detail.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(detail[Prefix.Length..]);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2
                    || root[0].ValueKind != JsonValueKind.String || root[1].ValueKind != JsonValueKind.String) return false;
                before = root[0].GetString()!;
                after = root[1].GetString()!;
                return true;
            }
            catch (JsonException) { return false; }
        }
        if (!detail.StartsWith("@@ ", StringComparison.Ordinal)) return false;
        int separator = detail.IndexOf(" ==> ", 3, StringComparison.Ordinal);
        if (separator < 0) return false;
        before = detail[3..separator].Replace("\\n", "\n", StringComparison.Ordinal);
        after = detail[(separator + 5)..].Replace("\\n", "\n", StringComparison.Ordinal);
        return true;
    }
}
