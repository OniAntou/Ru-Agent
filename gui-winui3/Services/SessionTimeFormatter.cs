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
}
