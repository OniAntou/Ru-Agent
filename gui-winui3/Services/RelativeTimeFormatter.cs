namespace RuApp.Services;

/// <summary>Compact relative timestamps for sidebar rows: just now, 8h, 3d, 2mo.</summary>
public static class RelativeTimeFormatter
{
    public static string Format(string updatedAt, DateTimeOffset now, string fallback = "")
    {
        if (!DateTimeOffset.TryParse(updatedAt, out var updated))
        {
            return fallback;
        }

        var delta = now - updated;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        return delta.TotalMinutes switch
        {
            < 2 => "just now",
            < 60 => $"{(int)delta.TotalMinutes}m",
            < 24 * 60 => $"{(int)delta.TotalHours}h",
            < 30 * 24 * 60 => $"{(int)delta.TotalDays}d",
            _ => $"{(int)(delta.TotalDays / 30)}mo",
        };
    }
}
