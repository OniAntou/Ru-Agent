using System.Globalization;

namespace RuApp.Services;

/// <summary>Absolute local timestamps for sidebar rows: "18:03" today, "17/09 18:03" this year, "17/09/2025 18:03" older.</summary>
public static class SessionTimeFormatter
{
    public static string Format(string updatedAt, DateTimeOffset now, string fallback = "")
    {
        if (!DateTimeOffset.TryParse(updatedAt, out var updated))
        {
            return fallback;
        }

        var local = updated.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today)
        {
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return local.Year == today.Year
            ? local.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How long a run took, at the precision the number actually carries: seconds under
    /// a minute, then minutes, then hours and minutes.
    /// </summary>
    /// <param name="duration">The measured span, or null when it was never measured.</param>
    /// <returns>
    /// A compact duration, or an empty string. An unreported duration is not zero
    /// seconds and must not be rendered as one.
    /// </returns>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } span || span < TimeSpan.Zero)
        {
            return string.Empty;
        }

        if (span.TotalSeconds < 60)
        {
            return $"{(int)span.TotalSeconds}s";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes}m";
        }

        return $"{(int)span.TotalHours}h {span.Minutes}m";
    }
}
