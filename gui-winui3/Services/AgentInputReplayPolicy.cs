using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RuApp.Services;

/// <summary>
/// Finds the question a run is waiting on when a session is opened. The durable
/// <c>input.requested</c> event sits behind the adoption cursor, so a waiting_input
/// run would otherwise come back with no way to answer it. The open path replays
/// the run's event log and re-arms the card from the newest request's payload.
/// </summary>
internal static class AgentInputReplayPolicy
{
    public const string InputRequestedEventType = "input.requested";

    /// <summary>
    /// The payload ref of the newest input request in one run's replayed event
    /// lines, or null when the run never asked. Later lines win: a second request
    /// supersedes the first.
    /// </summary>
    /// <param name="eventLines">Raw replay lines, SSE <c>data:</c> or plain JSON.</param>
    public static string? FindPendingInputPayloadRef(IEnumerable<string> eventLines)
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
                    || !string.Equals(eventType.GetString(), InputRequestedEventType, StringComparison.Ordinal)
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
