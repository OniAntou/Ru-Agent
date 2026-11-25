using System;
using System.Collections.Generic;
using System.Linq;
using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Which stored images belong to a reopened user message. The Host lists a run's images
/// with the steer that brought each (none for the run's own prompt); a replayed prompt takes
/// the images without a steer, and a replayed steer takes its own.
/// </summary>
internal static class ReplayImageAssignment
{
    /// <summary>At most this many images are fetched for one message, the bound of a turn.</summary>
    internal const int MaxImagesPerMessage = AgentRunLimits.MaxImages;

    public static IReadOnlyList<string> PayloadsFor(ChatMessageItem message, IReadOnlyList<AgentRunImagePayload> runImages)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(runImages);
        return runImages
            .Where(image => string.Equals(image.SteerId, message.SourceSteerId, StringComparison.Ordinal))
            .Select(image => image.PayloadId)
            .Take(MaxImagesPerMessage)
            .ToArray();
    }

    /// <summary>The steer a delivered steer activity (<c>act_&lt;hex&gt;_steer</c>) came from.</summary>
    public static string? SteerIdFromActivityId(string? activityId)
    {
        const string Prefix = "act_", Suffix = "_steer";
        if (activityId is null || !activityId.StartsWith(Prefix, StringComparison.Ordinal)
            || !activityId.EndsWith(Suffix, StringComparison.Ordinal)) return null;
        var steerId = AgentSteerLimits.IdPrefix + activityId[Prefix.Length..^Suffix.Length];
        return AgentSteerLimits.IsValidSteerId(steerId) ? steerId : null;
    }

    /// <summary>The image a stored data URL holds, or null when it is not a readable image.</summary>
    public static (string MimeType, byte[] Bytes)? DecodeDataUrl(string? dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:image/", StringComparison.Ordinal)) return null;
        var marker = dataUrl.IndexOf(";base64,", StringComparison.Ordinal);
        if (marker < 0) return null;
        try
        {
            var bytes = Convert.FromBase64String(dataUrl[(marker + 8)..]);
            return bytes.Length == 0 ? null : (dataUrl[5..marker], bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
