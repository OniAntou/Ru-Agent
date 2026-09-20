using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RuApp.Services;

/// <summary>
/// Finds the usage report to restore when a session is opened. Session
/// messages persist only role/content, so a freshly opened transcript has no
/// provider report and the context meter hides its system-plus-tools row.
/// The durable run events still carry every <c>model.usage_recorded</c>
/// report, so the open path replays the newest one instead of guessing.
/// </summary>
internal static class SessionUsageReplayPolicy
{
    public const string UsageRecordedEventType = "model.usage_recorded";

    /// <summary>
    /// The payload ref of the newest usage report in one run's replayed event
    /// lines, or null when the run reported none. Later lines win: usage
    /// totals only grow inside a run, so the last report covers the whole
    /// transcript while an earlier one would under-read the meter.
    /// </summary>
    /// <param name="eventLines">Raw replay lines, SSE <c>data:</c> or plain JSON.</param>
    public static string? FindLatestUsagePayloadRef(IEnumerable<string> eventLines)
    {
        ArgumentNullException.ThrowIfNull(eventLines);

        string? latest = null;
        foreach (var line in eventLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string json = line.StartsWith("data: ", StringComparison.Ordinal)
                ? line["data: ".Length..]
                : line;
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("event_type", out var eventType)
                    || eventType.ValueKind != JsonValueKind.String
                    || !string.Equals(eventType.GetString(), UsageRecordedEventType, StringComparison.Ordinal)
                    || !root.TryGetProperty("payload_ref", out var payloadRef)
                    || payloadRef.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(payloadRef.GetString()))
                {
                    continue;
                }

                latest = payloadRef.GetString();
            }
        }

        return latest;
    }
}
