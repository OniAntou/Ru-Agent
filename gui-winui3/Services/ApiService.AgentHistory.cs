using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

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
        /// <summary>A session run in transcript order, oldest-first.</summary>
        public sealed record SessionRunRef(string RunId, bool IsTerminal);

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
                        runs.Add(new SessionRunRef(runId.GetString()!, IsRunTerminal(status)));
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

        private static bool IsRunTerminal(string status) =>
            string.Equals(status, "completed", StringComparison.Ordinal)
            || string.Equals(status, "failed", StringComparison.Ordinal)
            || string.Equals(status, "cancelled", StringComparison.Ordinal)
            || string.Equals(status, "interrupted", StringComparison.Ordinal);

        public async Task<IReadOnlyList<AgentActivityModel>?> ListRunActivitiesAsync(string runId, CancellationToken cancellationToken)
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

                return rows;
            }
            catch (Exception ex)
            {
                LogApiError("ListRunActivitiesAsync", ex);
            }

            return null;
        }
    }
}
