using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using ProtocolActivityWire = RuAgent.Protocol.Harness.AgentActivityWire;
using ProtocolTodoWire = RuAgent.Protocol.Harness.AgentTodoWire;
using RuAgent.Protocol.Harness;
using RuBackend.Conversation;
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

/// <summary>
/// Replays the durable event stream of a finished (or running) run back into the
/// same activity rows the live feed showed. Activity frames are transient by design,
/// so a fresh client that only reads persisted session messages would otherwise see
/// the assistant prose with none of the tool rows that produced it.
/// </summary>
public sealed partial class AgentActivitiesService
{
    private readonly IHarnessEventStore _store;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly IMetadataPolicy _metadataPolicy;
    private readonly RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor _secrets;

    public AgentActivitiesService(IHarnessEventStore store, HarnessPayloadStore payloadStore,
        IMetadataPolicy metadataPolicy, RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor? secrets = null)
    {
        // Replay re-projects rows from stored proposals; their details get the live feed's redaction.
        _secrets = secrets ?? RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Empty;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _metadataPolicy = metadataPolicy ?? throw new ArgumentNullException(nameof(metadataPolicy));
    }

    /// <summary>The redactor replayed details pass through; the Host's own known-secret set.</summary>
    internal RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor Secrets => _secrets;

    /// <summary>
    /// How many activity rows one replay page carries, and the default page. A long run has
    /// far more: the first page holds the newest rows, because a reopened run should show what
    /// it was doing when the user came back, and a client reads older pages with
    /// <c>before_sequence</c> until <c>has_more</c> is false. Nothing is cut for good.
    /// </summary>
    internal const int MaxReplayedActivities = 500;

    /// <summary>
    /// How many narration chunks one replay page may carry. Narration is read per page, so the
    /// budget only has to cover one page: a model step yields at most one chunk, and a page
    /// holds at most <see cref="MaxReplayedActivities"/> rows. Past it the page's transcript is
    /// dropped and the caller falls back to the grouped layout for that page, never to a
    /// truncated sentence.
    /// </summary>
    private const int MaxTranscriptSegments = 512;

    /// <summary>
    /// Narration characters one replay page may carry, sized so a full page stays well inside
    /// the clients' 8 MiB response bound. Same fail-soft rule as the segment count.
    /// </summary>
    private const int MaxTranscriptChars = 512 * 1024;

    public Task<AgentServiceResult> ListRunActivitiesAsync(string runIdRaw, CancellationToken cancellationToken) =>
        ListRunActivitiesAsync(runIdRaw, beforeSequence: null, limit: null, cancellationToken);

    /// <summary>
    /// One page of a run's replayed activity, newest first across pages and chronological
    /// within one. Without <paramref name="beforeSequence"/> it is the newest page, led by the
    /// latest checklist; with it, the page just older than that durable sequence. The response
    /// names the next cursor (<c>next_before_sequence</c>) while older rows remain.
    /// </summary>
    public async Task<AgentServiceResult> ListRunActivitiesAsync(
        string runIdRaw, long? beforeSequence, int? limit, CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (beforeSequence is < 1)
        {
            return BoundedError(400, "invalid_cursor", "before_sequence must be a positive durable sequence.");
        }

        if (limit is < 1 or > MaxReplayedActivities)
        {
            return BoundedError(400, "invalid_limit", $"limit must be 1..{MaxReplayedActivities}.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        var proposed = new Dictionary<string, ProposedReplayCall>(StringComparer.Ordinal);
        var requestedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var completed = new List<CompletedReplayCall>();
        var completedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var stepStartedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var thinking = new List<ThinkingReplayRow>();
        var reasoning = new Dictionary<string, ReasoningReplayCall>(StringComparer.Ordinal);
        var stepTexts = new List<StepTextReplayRow>();
        var compactions = new List<CompactionReplayRow>();
        var steers = new List<(PayloadId PayloadId, long Sequence)>();

        await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            var evt = envelope.Event;
            switch (evt.Kind)
            {
                case "tool.proposed"
                    when evt.ToolCallId is { } proposedCallId
                        && evt.ModelStepId is { } proposedStep
                        && evt.ProviderCallId is { } providerCallId
                        && evt.PayloadId is { } argsPayloadId
                        && evt.SafeCount is { } modelOrder:
                    proposed[proposedCallId.Value] = new ProposedReplayCall(
                        proposedCallId,
                        providerCallId,
                        proposedStep,
                        modelOrder,
                        argsPayloadId,
                        evt.Sequence);
                    break;
                case "tool.execution_requested"
                    when evt.ExecutionId is { } requestedExecutionId:
                    requestedAt[requestedExecutionId.Value] = evt.OccurredAt;
                    break;
                case "tool.execution_completed"
                    when evt.ToolCallId is { } completedCallId
                        && evt.ProviderCallId is { } completedProviderCallId
                        && evt.ExecutionId is { } completedExecutionId:
                    completedCallIds.Add(completedCallId.Value);
                    completed.Add(new CompletedReplayCall(
                        completedCallId,
                        completedProviderCallId,
                        completedExecutionId,
                        evt.PayloadId,
                        evt.SafeCode,
                        evt.ToolOutcome,
                        evt.SideEffectsPossible,
                        evt.OccurredAt,
                        evt.Sequence));
                    break;
                case "model.step_started"
                    when evt.ModelStepId is { } startedStep:
                    stepStartedAt[startedStep.Value] = evt.OccurredAt;
                    break;
                case "model.step_completed"
                    when evt.ModelStepId is { } finishedStep:
                    stepStartedAt.TryGetValue(finishedStep.Value, out var startedAt);
                    int? durationMs = startedAt == default
                        ? null
                        : (int?)Math.Max(0, (evt.OccurredAt - startedAt).TotalMilliseconds);
                    thinking.Add(new ThinkingReplayRow(finishedStep, durationMs, evt.Sequence));
                    if (evt.PayloadId is { } stepPayloadId)
                    {
                        stepTexts.Add(new StepTextReplayRow(stepPayloadId, evt.Sequence));
                    }

                    break;
                case "model.reasoning_recorded"
                    when evt.ModelStepId is { } reasoningStep
                        && evt.PayloadId is { } reasoningPayloadId:
                    reasoning[reasoningStep.Value] = new ReasoningReplayCall(
                        reasoningPayloadId,
                        evt.Sequence);
                    break;
                case "continue.requested"
                    when evt.PayloadId is { } blockedPayloadId:
                    stepTexts.Add(new StepTextReplayRow(blockedPayloadId, evt.Sequence));
                    break;
                case "run.note_added"
                    when evt.PayloadId is { } steerPayloadId
                        && string.Equals(evt.SafeCode, RuAgent.Harness.Core.Steering.RunSteerContract.DeliveredSafeCode, StringComparison.Ordinal):
                    steers.Add((steerPayloadId, evt.Sequence));
                    break;
                case "context.compacted"
                    when string.Equals(evt.SafeCode, "context_compacted", StringComparison.Ordinal):
                    compactions.Add(new CompactionReplayRow(evt.Sequence, evt.SafeCount));
                    break;
            }
        }

        var before = beforeSequence ?? long.MaxValue;
        var candidates = compactions.Select(row => new ReplayCandidate(row.Sequence, Compaction: row))
            .Concat(steers.Select(row => new ReplayCandidate(row.Sequence, Steer: row.PayloadId)))
            .Concat(completed.Select(row => new ReplayCandidate(row.Sequence, Completed: row)))
            .Concat(proposed.Values.Where(row => !completedCallIds.Contains(row.ToolCallId.Value))
                .Select(row => new ReplayCandidate(row.Sequence, Pending: row)))
            .Concat(thinking.Select(row => new ReplayCandidate(row.Sequence, Thinking: row)))
            .Where(row => row.Sequence < before);
        var page = await RebuildNewestRowsAsync(runId, run.State.IsTerminal(), candidates,
            proposed, requestedAt, reasoning, limit ?? MaxReplayedActivities,
            pinLatestTodos: beforeSequence is null, cancellationToken).ConfigureAwait(false);
        var cappedRows = page.Rows;
        var wires = new List<JsonElement>(cappedRows.Count);
        foreach (var row in cappedRows)
        {
            JsonElement wire;
            try
            {
                // Replayed details get the live feed's redaction.
                using var document = JsonDocument.Parse(AgentActivityWireCodec.Serialize(MapActivity(
                    row.Activity with { Detail = AgentActivityDetailRedaction.Redact(row.Activity.Detail, _secrets) },
                    row.Sequence)));
                wire = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            wires.Add(wire);
        }

        // Narration follows the page's own window, so each page carries the words between its
        // rows and the narration budget applies per page instead of dropping a long run's
        // whole transcript. The oldest page also takes everything before its first row.
        var lower = page.HasMore ? page.OldestSequence ?? 0 : 0;
        var pageTexts = stepTexts.Where(row => row.Sequence >= lower && row.Sequence < before).ToList();
        var chunks = await ReadTranscriptChunksAsync(runId, pageTexts, cancellationToken).ConfigureAwait(false);
        var transcript = chunks is null
            ? Array.AsReadOnly(Array.Empty<TranscriptEntry>())
            : RunTranscriptPlanner.Merge(
                cappedRows.Select(row => (row.Sequence, row.Activity.ActivityId)).ToList(),
                chunks);
        return new AgentServiceResult(200, new
        {
            activities = wires,
            transcript,
            has_more = page.HasMore,
            next_before_sequence = page.HasMore ? page.OldestSequence : null,
            older_count = page.OlderCount,
        });
    }

    /// <summary>
    /// The model's own words in event order, so a reopened session can interleave
    /// narration with the tool rows instead of grouping every row above the text.
    /// </summary>
    /// <remarks>
    /// Session messages persist only the final step's text, while every intermediate
    /// narration lives solely in the durable step payloads read here. Anything
    /// unreadable is skipped; over budget (count or characters) drops the whole
    /// transcript and the caller falls back to the session text, so a pathological
    /// run degrades to the old grouped layout, never to a truncated sentence.
    /// </remarks>
    private async Task<IReadOnlyList<TranscriptChunk>?> ReadTranscriptChunksAsync(
        RunId runId,
        IReadOnlyList<StepTextReplayRow> stepTexts,
        CancellationToken cancellationToken)
    {
        if (stepTexts.Count == 0 || stepTexts.Count > MaxTranscriptSegments)
        {
            return null;
        }

        var chunks = new List<TranscriptChunk>(stepTexts.Count);
        int totalChars = 0;
        foreach (var row in stepTexts.OrderBy(item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadStepTextAsync(runId, row.PayloadId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            totalChars += text.Length;
            if (totalChars > MaxTranscriptChars)
            {
                return null;
            }

            chunks.Add(new TranscriptChunk(row.Sequence, text));
        }

        return chunks;
    }

    private async Task<string?> ReadStepTextAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        HarnessPayloadContent content;
        try
        {
            content = await _payloadStore.ReadForClientAsync(runId, payloadId, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException)
        {
            return null;
        }

        if (!content.Audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient))
        {
            return null;
        }

        string? raw;
        if (content.Kind == HarnessPayloadKind.AssistantStep && content.Encoding == HarnessPayloadEncoding.Json)
        {
            AssistantStep step;
            try
            {
                step = AssistantStepJsonCodec.Deserialize(content.Plaintext);
            }
            catch (InvalidDataException)
            {
                return null;
            }

            raw = step.Text;
        }
        else if (content.Kind == HarnessPayloadKind.AssistantText && content.Encoding == HarnessPayloadEncoding.Utf8Text)
        {
            try
            {
                raw = Encoding.UTF8.GetString(content.Plaintext.Span);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = _metadataPolicy.StripVoiceMarkers(raw);
        if (string.IsNullOrWhiteSpace(cleaned)
            || string.Equals(cleaned, MetadataPolicy.EmptyResponseText, StringComparison.Ordinal))
        {
            return null;
        }

        return cleaned;
    }

    private async Task<ToolProposal?> RebuildProposalAsync(
        RunId runId,
        ProposedReplayCall call,
        CancellationToken cancellationToken)
    {
        HarnessPayloadContent args;
        try
        {
            args = await _payloadStore.ReadForClientAsync(runId, call.ArgsPayloadId, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException)
        {
            return null;
        }

        if (args.Kind != HarnessPayloadKind.ToolArguments
            || !args.Audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient)
            || args.Encoding == HarnessPayloadEncoding.Opaque)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(args.Plaintext);
            var root = document.RootElement;
            if (!root.TryGetProperty("tool_id", out var toolIdElement)
                || !root.TryGetProperty("tool_version", out var toolVersionElement)
                || !root.TryGetProperty("arguments", out var argumentsElement)
                || toolIdElement.ValueKind != JsonValueKind.String
                || toolVersionElement.ValueKind != JsonValueKind.String
                || argumentsElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ToolProposal(
                runId,
                call.ModelStepId,
                call.ToolCallId,
                call.ProviderCallId,
                call.ModelOrder,
                toolIdElement.GetString() ?? string.Empty,
                toolVersionElement.GetString() ?? string.Empty,
                argumentsElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<ToolExecutionReceipt?> RebuildReceiptAsync(
        RunId runId,
        CompletedReplayCall call,
        CancellationToken cancellationToken)
    {
        if (call.ToolOutcome is null || call.SideEffectsPossible is null)
        {
            return null;
        }

        ReadOnlyMemory<byte> modelContent = ReadOnlyMemory<byte>.Empty;
        HarnessPayloadEncoding encoding = HarnessPayloadEncoding.Json;
        if (call.ResultPayloadId is { } payloadId)
        {
            HarnessPayloadContent result;
            try
            {
                result = await _payloadStore.ReadForClientAsync(runId, payloadId, cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessStoreException)
            {
                return null;
            }

            if (result.Kind != HarnessPayloadKind.ToolOutput
                || !result.Audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient)
                || result.Encoding == HarnessPayloadEncoding.Opaque)
            {
                return null;
            }

            modelContent = result.Plaintext.ToArray();
            encoding = result.Encoding;
        }

        try
        {
            return new ToolExecutionReceipt(
                call.ExecutionId,
                call.ToolCallId,
                call.ToolOutcome.Value,
                call.SafeCode ?? "succeeded",
                call.ResultPayloadId,
                call.SideEffectsPossible.Value,
                modelContent,
                encoding,
                call.ProviderCallId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The live feed shows the edit preview from the Started frame and keeps it
    /// when the completion lands. A replay only has one row per call, so carry
    /// the same preview over instead of dropping the diff card.
    /// </summary>
    private static AgentActivity WithEditPreview(AgentActivity activity, ToolProposal proposal)
    {
        if (activity.Detail is not null
            || proposal.ToolId is not ("edit" or "write" or "edit_file" or "write_file" or "apply_patch"))
        {
            return activity;
        }

        var startedPreview = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);
        return startedPreview.Detail is null ? activity : activity with { Detail = startedPreview.Detail };
    }

    /// <summary>
    /// Reads a recorded reasoning excerpt for activity replay. Anything that is
    /// not a client-visible UTF-8 reasoning payload is skipped, never surfaced
    /// as a failure: replay must degrade to a duration-only thinking row.
    /// </summary>
    private async Task<string?> ReadSteerTextAsync(RunId runId, PayloadId payloadId, CancellationToken cancellationToken)
    {
        HarnessPayloadContent content;
        try
        {
            content = await _payloadStore.ReadForClientAsync(runId, payloadId, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException)
        {
            return null;
        }

        return content.Kind == HarnessPayloadKind.UserPrompt && content.Encoding == HarnessPayloadEncoding.Utf8Text
            ? Encoding.UTF8.GetString(content.Plaintext.Span)
            : null;
    }

    private async Task<string?> ReadReasoningDetailAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        HarnessPayloadContent content;
        try
        {
            content = await _payloadStore.ReadForClientAsync(runId, payloadId, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException)
        {
            return null;
        }

        if (content.Kind != HarnessPayloadKind.ModelReasoning
            || !content.Audience.HasFlag(HarnessPayloadAudience.AuthenticatedClient)
            || content.Encoding == HarnessPayloadEncoding.Opaque
            || content.Plaintext.IsEmpty)
        {
            return null;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(content.Plaintext.Span);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static ProtocolActivityWire MapActivity(AgentActivity activity, long durableSequence) =>
        new(
            activity.ActivityId,
            activity.Kind,
            activity.State,
            activity.FileName,
            activity.LineStart,
            activity.LineEnd,
            activity.Symbol,
            activity.Count,
            activity.Added,
            activity.Removed,
            activity.DurationMs,
            activity.SafeCode,
            activity.ToolDisplay,
            activity.Detail,
            activity.Todos?.Select(todo => new ProtocolTodoWire(todo.Content, todo.Status)).ToArray(),
            durableSequence,
            activity.ExitCode,
            activity.RelativePath,
            activity.ReasoningPayloadId);

    private sealed record StepTextReplayRow(PayloadId PayloadId, long Sequence);

    private sealed record ProposedReplayCall(
        ToolCallId ToolCallId,
        string ProviderCallId,
        ModelStepId ModelStepId,
        int ModelOrder,
        PayloadId ArgsPayloadId,
        long Sequence);

    private sealed record CompletedReplayCall(
        ToolCallId ToolCallId,
        string ProviderCallId,
        ExecutionId ExecutionId,
        PayloadId? ResultPayloadId,
        string? SafeCode,
        ToolExecutionOutcome? ToolOutcome,
        bool? SideEffectsPossible,
        DateTimeOffset CompletedAt,
        long Sequence);

    private sealed record ThinkingReplayRow(ModelStepId ModelStepId, int? DurationMs, long Sequence);

    private sealed record ReasoningReplayCall(PayloadId PayloadId, long Sequence);

    private sealed record ReplayedActivityRow(long Sequence, AgentActivity Activity);
    private sealed record CompactionReplayRow(long Sequence, int? CoveredCount);
}
