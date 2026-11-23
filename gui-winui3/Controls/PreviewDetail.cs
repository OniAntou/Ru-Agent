using System.Globalization;

namespace RuApp.Controls;

/// <summary>
/// The preview header's secondary line: what the file is, how big it is and when it
/// last changed. Pure, so the rounding and the wording are testable without a window.
/// </summary>
public static class PreviewDetail
{
    /// <summary>The parts that are present, joined by the one separator the surface uses.</summary>
    public static string Compose(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>A size a reader can hold in their head: "812 B", "12.4 KB", "2 MB".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0) return string.Empty;
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double value = bytes / 1024d;
        string unit = "KB";
        if (value >= 1024)
        {
            value /= 1024d;
            unit = "MB";
        }

        if (value >= 1024)
        {
            value /= 1024d;
            unit = "GB";
        }

        return value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + unit;
    }

    /// <summary>
    /// How long ago the file changed, from two local times. Under a minute reads as
    /// "just now"; past a month the date says more than the distance.
    /// </summary>
    public static string Modified(DateTime modifiedLocal, DateTime nowLocal)
    {
        var elapsed = nowLocal - modifiedLocal;
        // A file restored from a backup can carry a time ahead of this machine's clock.
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed < TimeSpan.FromMinutes(1)) return "modified just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"modified {(int)elapsed.TotalMinutes} min ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"modified {(int)elapsed.TotalHours} h ago";
        if (elapsed < TimeSpan.FromDays(2)) return "modified yesterday";
        if (elapsed < TimeSpan.FromDays(30)) return $"modified {(int)elapsed.TotalDays} d ago";
        return "modified " + modifiedLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>An image's natural size, without thousands separators inside a count.</summary>
    public static string Dimensions(int width, int height) =>
        width > 0 && height > 0
            ? width.ToString(CultureInfo.InvariantCulture) + " × " + height.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>"1 line" or "1,204 lines".</summary>
    public static string Lines(int count) =>
        count == 1 ? "1 line" : count.ToString("N0", CultureInfo.InvariantCulture) + " lines";

    /// <summary>"1 page" or "12 pages".</summary>
    public static string Pages(int count) =>
        count == 1 ? "1 page" : count.ToString("N0", CultureInfo.InvariantCulture) + " pages";
}
