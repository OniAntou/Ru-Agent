using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services
{
    /// <summary>
    /// Replays durable run activity for transcript rebuilds. Session messages persist
    /// only role/content, while activity rows are transient — so a reloaded session
    /// would otherwise lose every tool row. These reads are best-effort: any failure
    /// keeps the text-only transcript instead of a partial feed.
    /// </summary>
    public partial class ApiService
    {
        /// <summary>
        /// A session run in transcript order, oldest-first.
        /// </summary>
        /// <param name="RunId">The run's id.</param>
        /// <param name="IsTerminal">Whether the run has ended.</param>
        /// <param name="Status">The wire status, verbatim: succeeded, failed, interrupted, cancelled, running.</param>
        /// <param name="CreatedAt">When the run started, or null when the host did not say.</param>
        /// <param name="TerminalAt">When the run ended, or null while it is still live.</param>
        /// <remarks>
        /// Status and the two timestamps are already on the wire - the report read them
        /// away and then had to describe a run that failed as "no provider report",
        /// which is true of every run that failed and says nothing about which.
        /// </remarks>
        public sealed record SessionRunRef(
            string RunId,
            bool IsTerminal,
            string Status,
            DateTimeOffset? CreatedAt,
            DateTimeOffset? TerminalAt);

        public async Task<IReadOnlyList<SessionRunRef>?> ListSessionRunIdsAsync(string sessionId, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _httpClient.GetAsync(
                    $"{_baseUrl}/agent/v1/sessions/{Uri.EscapeDataString(sessionId)}/runs?limit=100", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!document.RootElement.TryGetProperty("items", out var items)
                    || items.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var runs = new List<SessionRunRef>();
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("run_id", out var runId)
                        && runId.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(runId.GetString()))
                    {
                        string status = item.TryGetProperty("status", out var statusElement)
                            && statusElement.ValueKind == JsonValueKind.String
                            ? statusElement.GetString() ?? string.Empty
                            : string.Empty;
                        runs.Add(new SessionRunRef(
                            runId.GetString()!,
                            IsRunTerminal(status),
                            status,
                            ReadTimestamp(item, "created_at"),
                            ReadTimestamp(item, "terminal_at")));
                    }
                }

                // Newest-first on the wire; the transcript aligns oldest-first.
                runs.Reverse();
                return runs;
            }
            catch (Exception ex)
            {
                LogApiError("ListSessionRunIdsAsync", ex);
            }

            return null;
        }

        /// <summary>
        /// One durable run state, or null when it cannot be read. Used by the session
        /// open to adopt a run that outlived the host: any failure returns null so the
        /// session still opens, just without a run in hand.
        /// </summary>
        public async Task<AgentRunModel?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _httpClient.GetAsync(
                    $"{_baseUrl}/agent/v1/runs/{Uri.EscapeDataString(runId)}", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var payload = await JsonSerializer.DeserializeAsync<GetAgentRunResponse>(
                    stream,
                    cancellationToken: cancellationToken);
                return payload is null ? null : AgentRunModel.FromGetResponse(payload);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("GetRunAsync", ex);
            }

            return null;
        }

        /// <summary>
        /// Reads one ISO-8601 timestamp off a wire object, or null when the field is
        /// absent, null, or not a timestamp. A malformed value is a missing value: a
        /// run with an unreadable start time still has a real duration to report.
        /// </summary>
        private static DateTimeOffset? ReadTimestamp(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;

        /// <summary>
        /// The newest provider usage report for a session, oldest runs first on
        /// the wire so the walk goes newest-first and stops at the first run
        /// that reported. Its last report covers the whole transcript, which is
        /// what restores the meter's system-plus-tools row on open. Best-effort:
        /// any failure returns null and the meter falls back to its estimate.
        /// </summary>
        public async Task<AgentModelUsage?> GetLatestSessionUsageAsync(string sessionId, CancellationToken cancellationToken)
        {
            try
            {
                var runs = await ListSessionRunIdsAsync(sessionId, cancellationToken);
                if (runs is null || runs.Count == 0)
                {
                    return null;
                }

                for (int index = runs.Count - 1; index >= 0; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var usage = await GetLatestRunUsageAsync(runs[index].RunId, cancellationToken);
                    if (usage is not null)
                    {
                        return usage;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogApiError("GetLatestSessionUsageAsync", ex);
            }

            return null;
        }

        /// <summary>
        /// The newest runs of a session with the usage each one reported, oldest
        /// first, so the panel can show both the per-run figures and their totals.
        ///
        /// Bounded on purpose: each run costs two reads — its events and the usage
        /// payload — so the walk covers a tail of the session rather than all of it.
        /// A run that cannot be read stays as a row with no report instead of taking
        /// the panel down, and a transport failure returns null so the flyout can say
        /// it could not read rather than claim there was nothing to read.
        /// </summary>
        public async Task<IReadOnlyList<SessionUsageRow>?> ListSessionUsageAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            try
            {
                var runs = await ListSessionRunIdsAsync(sessionId, cancellationToken);
                if (runs is null)
                {
                    return null;
                }

                int firstIndex = Math.Max(0, runs.Count - SessionUsagePolicy.MaxReportedRuns);
                var rows = new List<SessionUsageRow>(runs.Count - firstIndex);
                for (int index = firstIndex; index < runs.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var run = runs[index];

                    // A live run has no durable report yet, so asking would only cost
                    // a read to learn what is already known.
                    AgentModelUsage? usage = null;
                    if (run.IsTerminal)
                    {
                        try
                        {
                            usage = await GetLatestRunUsageAsync(run.RunId, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            LogApiError("GetLatestRunUsageAsync", ex);
                        }
                    }

                    rows.Add(new SessionUsageRow(
                        run.RunId,
                        run.IsTerminal,
                        run.Status,
                        run.CreatedAt,
                        run.TerminalAt,
                        usage));
                }

                return rows;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogApiError("ListSessionUsageAsync", ex);
            }

            return null;
        }

        private async Task<AgentModelUsage?> GetLatestRunUsageAsync(string runId, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(
                $"{_baseUrl}/agent/v1/runs/{Uri.EscapeDataString(runId)}/events?live=0&after_sequence=0", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            // Bounded read: the replay body is a run's whole event log and an
            // unbounded string would let a large one pin memory on the UI thread.
            var body = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken);
            var lines = body.Split('\n');
            string? payloadRef = SessionUsageReplayPolicy.FindLatestUsagePayloadRef(lines);
            if (string.IsNullOrWhiteSpace(payloadRef))
            {
                return null;
            }

            using var payloadResponse = await _httpClient.GetAsync(
                $"{_baseUrl}/agent/v1/runs/{Uri.EscapeDataString(runId)}/payloads/{Uri.EscapeDataString(payloadRef)}", cancellationToken);
            if (!payloadResponse.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = await payloadResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            string content;
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                content = contentElement.GetString() ?? string.Empty;
            }
            else
            {
                content = document.RootElement.GetRawText();
            }

            return AgentModelUsage.Parse(content);
        }

        private static bool IsRunTerminal(string status) =>
            string.Equals(status, "completed", StringComparison.Ordinal)
            || string.Equals(status, "failed", StringComparison.Ordinal)
            || string.Equals(status, "cancelled", StringComparison.Ordinal)
            || string.Equals(status, "interrupted", StringComparison.Ordinal);

        public async Task<RunActivityReplay?> ListRunActivitiesAsync(string runId, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _httpClient.GetAsync(
                    $"{_baseUrl}/agent/v1/runs/{Uri.EscapeDataString(runId)}/activities", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!document.RootElement.TryGetProperty("activities", out var items)
                    || items.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var rows = new List<AgentActivityModel>();
                foreach (var item in items.EnumerateArray())
                {
                    AgentActivityWire wire;
                    try
                    {
                        wire = AgentActivityWireCodec.Parse(item.GetRawText());
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    rows.Add(new AgentActivityModel(
                        wire.ActivityId,
                        wire.Kind,
                        wire.State,
                        wire.FileName,
                        wire.LineStart,
                        wire.LineEnd,
                        wire.Symbol,
                        wire.Count,
                        wire.Added,
                        wire.Removed,
                        wire.DurationMs,
                        wire.SafeCode,
                        wire.ToolDisplay,
                        DurableSequence: null,
                        TransientSequence: null,
                        wire.Detail,
                        wire.Todos));
                }

                return new RunActivityReplay(rows, ReadTranscript(document.RootElement));
            }
            catch (Exception ex)
            {
                LogApiError("ListRunActivitiesAsync", ex);
            }

            return null;
        }

        /// <summary>
        /// One ordered replay unit from the run transcript: narration the model spoke
        /// between tool rows. Lenient by design: a malformed entry is skipped and a
        /// missing array means the host predates the transcript, which the caller
        /// renders as the legacy grouped feed.
        /// </summary>
        public sealed record RunTranscriptEntry(long Sequence, string Kind, string? Text, string? ActivityId);

        /// <summary>
        /// A run's durable replay: its activity rows plus the narration transcript
        /// that interleaves them. The transcript is additive: older hosts omit it
        /// and the transcript rebuild falls back to the grouped layout.
        /// </summary>
        public sealed record RunActivityReplay(
            IReadOnlyList<AgentActivityModel> Activities,
            IReadOnlyList<RunTranscriptEntry> Transcript);

        private static IReadOnlyList<RunTranscriptEntry> ReadTranscript(JsonElement root)
        {
            var entries = new List<RunTranscriptEntry>();
            if (!root.TryGetProperty("transcript", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return entries;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                long sequence = item.TryGetProperty("sequence", out var sequenceElement)
                    && sequenceElement.ValueKind == JsonValueKind.Number
                    && sequenceElement.TryGetInt64(out var parsed)
                    ? parsed
                    : 0;
                string kind = item.TryGetProperty("kind", out var kindElement)
                    && kindElement.ValueKind == JsonValueKind.String
                    ? kindElement.GetString() ?? string.Empty
                    : string.Empty;
                string? text = item.TryGetProperty("text", out var textElement)
                    && textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString()
                    : null;
                string? activityId = item.TryGetProperty("activity_id", out var activityElement)
                    && activityElement.ValueKind == JsonValueKind.String
                    ? activityElement.GetString()
                    : null;

                if (string.Equals(kind, "text", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(text))
                {
                    entries.Add(new RunTranscriptEntry(sequence, kind, text, null));
                }
                else if (string.Equals(kind, "activity", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(activityId))
                {
                    entries.Add(new RunTranscriptEntry(sequence, kind, null, activityId));
                }
            }

            return entries;
        }
    }
}
