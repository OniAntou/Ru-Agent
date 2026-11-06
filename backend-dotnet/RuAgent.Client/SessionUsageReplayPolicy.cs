using System.Text.Json;

namespace RuAgent.Client;

/// <summary>One usage report found in a run's replayed event log.</summary>
/// <param name="Sequence">Durable event sequence, so a reader can order reports.</param>
/// <param name="ModelStepId">The step that produced the report, when the Host named one.</param>
/// <param name="PayloadRef">Handle for the report's payload.</param>
public readonly record struct SessionUsageReport(long Sequence, string? ModelStepId, string PayloadRef);

/// <summary>
/// Finds the usage report to restore when a session is opened.
/// </summary>
/// <remarks>
/// Session messages persist only role and content, so a freshly opened transcript has no
/// provider report or request breakdown for the context meter. The durable run events still
/// carry every <c>model.usage_recorded</c> report, so the open path replays the newest one
/// instead of guessing. Shared by the Desktop feed and the terminal UI so both surfaces
/// restore the same reading from the same log.
/// </remarks>
public static class SessionUsageReplayPolicy
{
    public const string UsageRecordedEventType = "model.usage_recorded";

    /// <summary>Bound on distinct reports read from one run's log.</summary>
    private const int MaximumReports = 4096;

    /// <summary>
    /// The newest usage report in one run's replayed event lines, or null when the run
    /// reported none. This is the latest step's context-window reading, not cumulative
    /// billing for the run, so it follows line order rather than the distinct-payload set.
    /// </summary>
    /// <param name="eventLines">Raw replay lines, SSE <c>data:</c> or plain JSON.</param>
    public static SessionUsageReport? FindLatestUsageReport(IEnumerable<string> eventLines)
    {
        ArgumentNullException.ThrowIfNull(eventLines);

        SessionUsageReport? latest = null;
        foreach (var line in eventLines)
        {
            if (TryParseReport(line, out var report)) latest = report;
        }

        return latest;
    }

    /// <summary>
    /// The payload ref of the newest usage report in one run's replayed event lines, or
    /// null when the run reported none.
    /// </summary>
    /// <param name="eventLines">Raw replay lines, SSE <c>data:</c> or plain JSON.</param>
    public static string? FindLatestUsagePayloadRef(IEnumerable<string> eventLines) =>
        FindLatestUsageReport(eventLines)?.PayloadRef;

    /// <summary>
    /// Every distinct usage report in one run's replayed event lines, in the order each
    /// payload first appeared.
    /// </summary>
    public static IReadOnlyList<string> FindUsagePayloadRefs(IEnumerable<string> eventLines) =>
        FindUsageReports(eventLines).Select(report => report.PayloadRef).ToArray();

    /// <summary>
    /// Every distinct usage report in one run's replayed event lines, in the order each
    /// payload first appeared. A report without a payload ref is skipped: a later empty
    /// one must not shadow an earlier usable one.
    /// </summary>
    public static IReadOnlyList<SessionUsageReport> FindUsageReports(IEnumerable<string> eventLines)
    {
        ArgumentNullException.ThrowIfNull(eventLines);

        var reports = new List<SessionUsageReport>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in eventLines)
        {
            if (!TryParseReport(line, out var report) || !seen.Add(report.PayloadRef))
            {
                continue;
            }

            reports.Add(report);
            if (reports.Count > MaximumReports) throw new InvalidDataException("usage_report_limit");
        }

        return reports;
    }

    /// <summary>
    /// Parses one replay line into a usage report. Malformed lines, other event types and
    /// reports without a payload ref are not reports.
    /// </summary>
    private static bool TryParseReport(string? line, out SessionUsageReport report)
    {
        report = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var json = line.StartsWith("data: ", StringComparison.Ordinal)
            ? line["data: ".Length..]
            : line;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
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
                return false;
            }

            var sequence = root.TryGetProperty("sequence", out var sequenceElement)
                && sequenceElement.TryGetInt64(out var parsed) ? parsed : 0;
            var stepId = root.TryGetProperty("model_step_id", out var stepElement)
                && stepElement.ValueKind == JsonValueKind.String ? stepElement.GetString() : null;
            report = new SessionUsageReport(sequence, stepId, payloadRef.GetString()!);
            return true;
        }
    }
}
