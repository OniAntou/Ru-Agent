using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

internal sealed record ChatRunUsage(GetAgentRunResponse Run, IReadOnlyList<ChatTokenUsage> Reports,
    ChatTokenUsage? LatestContext, decimal? Cost, string? Notice, bool ContextInvalidated = false);
internal sealed record ChatUsageReading(string Session, IReadOnlyList<ChatRunUsage> Runs, int ListedRuns);

/// <summary>Bounded, on-demand reads. Usage payloads never become model or tool input.</summary>
internal sealed class ChatUsageReader(AgentApiClient agents, AgentSseClient events,
    Func<string?, string, CancellationToken, Task<CatalogModelDetails?>> rates)
{
    internal const int MaxRuns = 8;
    internal const int MaxReports = 256;
    internal const int MaxFrames = 8192;
    private const int MaxReplayCharacters = 8 * 1024 * 1024;

    internal async Task<ChatUsageReading> ReadAsync(string session, string? preferredRun, bool contextOnly, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session)) return new(session, [], 0);
        var ids = (await agents.ListSessionRunIdsAsync(session, ct).ConfigureAwait(false)).Distinct(StringComparer.Ordinal).ToList();
        if (preferredRun is not null && !ids.Contains(preferredRun, StringComparer.Ordinal)) ids.Add(preferredRun);
        var selected = contextOnly ? new[] { preferredRun ?? ids.LastOrDefault() }.OfType<string>() : ids.TakeLast(MaxRuns);
        var rows = new List<ChatRunUsage>();
        foreach (var id in selected)
        {
            var run = await agents.GetRunAsync(id, ct).ConfigureAwait(false);
            if (run.SessionId != session || run.RunId != id)
                throw new AgentApiException("run_session_mismatch", "Usage belongs to another conversation.");
            try { rows.Add(await ReadRunAsync(run, contextOnly, ct).ConfigureAwait(false)); }
            catch (AgentApiException error) { rows.Add(new(run, [], null, null, error.ErrorCode)); }
            catch (HttpRequestException) { rows.Add(new(run, [], null, null, "usage_read_failed")); }
        }
        return new(session, rows, ids.Count);
    }

    private async Task<ChatRunUsage> ReadRunAsync(GetAgentRunResponse run, bool contextOnly, CancellationToken ct)
    {
        var references = new List<(string Id, long Sequence)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new Dictionary<long, string>();
        var frames = 0; long characters = 0; long boundary = 0; long lastSequence = 0;
        await foreach (var frame in events.ReadEventsAsync(run.RunId, 0, ct, live: false).ConfigureAwait(false))
        {
            if (++frames > MaxFrames || (characters += frame.Data.Length) > MaxReplayCharacters) throw Limit();
            if (frame.IsTransient) continue;
            HarnessSafeEventEnvelope envelope;
            try { envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data) ?? throw Invalid(); }
            catch (JsonException) { throw Invalid(); }
            if (envelope.RunId != run.RunId || envelope.SessionId != run.SessionId
                || envelope.Sequence <= 0 || envelope.Sequence != frame.DurableSequence) throw Invalid();
            if (sequences.TryGetValue(envelope.Sequence, out var previous))
            {
                if (previous != frame.Data) throw Invalid();
                continue;
            }
            if (envelope.Sequence <= lastSequence) throw Invalid();
            sequences.Add(envelope.Sequence, frame.Data);
            lastSequence = envelope.Sequence;
            if (envelope.EventType == "context.compacted") boundary = envelope.Sequence;
            if (envelope.EventType != "model.usage_recorded") continue;
            var reference = envelope.PayloadRef;
            if (reference is not { Length: > 0 and <= 128 } || reference.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-')))
                throw Invalid();
            if (!seen.Add(reference)) continue;
            // Every step reports usage, so a long run outgrows the bound. The context
            // reading needs only the newest report and keeps a sliding window; billing
            // fails closed rather than showing a cheap partial total.
            if (references.Count >= MaxReports)
            {
                if (!contextOnly) throw Limit();
                references.RemoveAt(0);
            }
            references.Add((reference, envelope.Sequence));
        }
        var reports = new List<ChatTokenUsage>();
        ChatTokenUsage? latest = null;
        // Context is the latest request, so historical billing payloads and catalog prices are unnecessary.
        var requestedReports = contextOnly ? references.Where(reference => reference.Sequence > boundary).TakeLast(1) : references;
        foreach (var reference in requestedReports)
        {
            var payload = await agents.GetPayloadAsync(run.RunId, reference.Id, ct).ConfigureAwait(false);
            if ((characters += payload.Length) > MaxReplayCharacters) throw Limit();
            var report = ChatTokenUsage.Parse(payload);
            reports.Add(report);
            if (reference.Sequence > boundary) latest = report;
        }
        decimal? cost = null;
        var models = reports.Select(report => report.Model ?? run.ProviderModel).Distinct(StringComparer.Ordinal).ToArray();
        if (!contextOnly && reports.Count > 0 && models.Length == 1 && !string.IsNullOrWhiteSpace(models[0]) && !string.IsNullOrWhiteSpace(run.ProviderBackend))
        {
            CatalogModelDetails? rate = null;
            using var priceDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            priceDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            try { rate = await rates(run.ProviderBackend, models[0]!, priceDeadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (AgentApiException) { }
            catch (HttpRequestException) { }
            var prices = reports.Select(report => report.Price(rate)).ToArray();
            if (prices.All(price => price is not null)) cost = prices.Sum(price => price!.Value);
        }
        var invalidated = boundary > 0 && latest is null;
        return new(run, reports, latest, cost, invalidated && contextOnly
            ? "Context was compacted; waiting for a new request report."
            : reports.Count == 0 ? "No provider usage report." : null, invalidated);
    }

    private static AgentApiException Limit() => new("usage_limit_exceeded", "Usage replay exceeded its display budget.");
    private static AgentApiException Invalid() => new("usage_replay_invalid", "Usage replay identity or order is invalid.");
}
