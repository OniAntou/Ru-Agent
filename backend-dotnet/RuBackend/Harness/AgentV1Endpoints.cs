using System.Text.Json;
using System.Globalization;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;
using RuBackend.Security;

namespace RuBackend.Harness;

public static partial class AgentV1Endpoints
{
    private const int MaxJsonBodyBytes = 64 * 1024;
    private const int MaxJsonBodyBytesDefault = MaxJsonBodyBytes;

    // Four data URLs at the per-image character cap (5,600,000) plus the JSON
    // envelope. The service-level 4MB/20MB image caps are only reachable when the
    // transport cap is at least this large.
    private const int MaxJsonBodyBytesAgentRun = 24 * 1024 * 1024;

    public static void Map(WebApplication app)
    {
        MapSubagents(app);
        MapSkills(app);
        MapPlugins(app);
        app.MapPost("/agent/v1/runs", CreateRunAsync);
        app.MapPost("/agent/v1/runs/selected", CreateSelectedRunAsync);
        app.MapGet("/agent/v1/runs/{runId}", GetRunAsync);
        app.MapGet("/agent/v1/runs/{runId}/events", GetEventsAsync);
        app.MapGet("/agent/v1/runs/{runId}/activities", GetRunActivitiesAsync);
        app.MapGet("/agent/v1/runs/{runId}/payloads/{payloadId}", GetPayloadAsync);
        app.MapPost("/agent/v1/runs/{runId}/approvals/{approvalId}", PostApprovalAsync);
        app.MapPost("/agent/v1/runs/{runId}/inputs/{inputId}", PostInputAsync);
        app.MapGet("/agent/v1/runs/{runId}/steers", GetSteersAsync);
        app.MapPost("/agent/v1/runs/{runId}/steers", PostSteerAsync);
        app.MapPost("/agent/v1/runs/{runId}/trust", PostTrustAsync);
        app.MapPost("/agent/v1/runs/{runId}/cancel", PostCancelAsync);
        app.MapPost("/agent/v1/runs/{runId}/compact", PostCompactAsync);
        app.MapPost("/agent/v1/runs/{runId}/commands", PostCommandAsync);
        app.MapGet("/agent/v1/sessions/{sessionId}/runs", GetSessionRunsAsync);
        app.MapGet("/agent/v1/sessions/{sessionId}/preset", GetSessionPresetAsync);
        app.MapGet("/agent/v1/sessions/{sessionId}/advisor", GetSessionAdvisorAsync);
        app.MapPut("/agent/v1/sessions/{sessionId}/advisor", PutSessionAdvisorAsync);
        app.MapGet("/agent/v1/policies", GetPoliciesAsync);
        app.MapPost("/agent/v1/policies", PostPolicyAsync);
        app.MapDelete("/agent/v1/policies/{ruleId}", DeletePolicyAsync);
        app.MapGet("/agent/v1/workspaces/{workspaceId}/concurrency", GetWorkspaceConcurrencyAsync);
        app.MapPut("/agent/v1/workspaces/{workspaceId}/concurrency", PutWorkspaceConcurrencyAsync);
        app.MapGet("/agent/v1/workspaces/{workspaceId}/activity", GetWorkspaceActivityAsync);
        app.MapGet("/agent/v1/workspaces/{workspaceId}/changes", GetWorkspaceChangesAsync);
        app.MapPost("/agent/v1/workspaces/{workspaceId}/changes/{changeId}/restore", PostWorkspaceChangeRestoreAsync);
    }

    private static Task<IResult> CreateRunAsync(HttpContext context, AgentRunCreationService service, CancellationToken ct) =>
        CreateRunCoreAsync(context, service, ct, requireProviderSelection: false);

    private static Task<IResult> CreateSelectedRunAsync(HttpContext context, AgentRunCreationService service, CancellationToken ct) =>
        CreateRunCoreAsync(context, service, ct, requireProviderSelection: true);

    private static async Task<IResult> CreateRunCoreAsync(HttpContext context, AgentRunCreationService service, CancellationToken ct, bool requireProviderSelection)
    {
        CreateAgentRunRequest? dto;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct, MaxJsonBodyBytesAgentRun);
            dto = text is null
                ? null
                : JsonSerializer.Deserialize<CreateAgentRunRequest>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Json(new { error_code = "request_too_large" }, statusCode: 413);
        }
        catch (JsonException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }
        catch (DecoderFallbackException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        if (dto is null)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }
        if (requireProviderSelection && dto.ProviderSelection is null)
            return Results.Json(new { error_code = "provider_intent_required" }, statusCode: 400);

        var result = await service.CreateRunAsync(dto, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetWorkspaceConcurrencyAsync(string workspaceId, AgentWorkspaceService service, CancellationToken ct)
    {
        var result = await service.GetWorkspaceConcurrencyAsync(workspaceId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PutWorkspaceConcurrencyAsync(string workspaceId, HttpContext context, AgentWorkspaceService service, CancellationToken ct)
    {
        WorkspaceConcurrencyUpdateRequest? request;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct);
            request = text is null
                ? null
                : JsonSerializer.Deserialize<WorkspaceConcurrencyUpdateRequest>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Json(new { error_code = "request_too_large" }, statusCode: 413);
        }
        catch (JsonException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }
        catch (DecoderFallbackException)
        {
            // The bounded body reader decodes strict UTF-8; malformed bytes are a
            // client error, not an unhandled exception (500).
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        if (request is null)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        var result = await service.UpdateWorkspaceConcurrencyAsync(workspaceId, request, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static Task<IResult> GetWorkspaceActivityAsync(string workspaceId, HttpContext context, AgentWorkspaceService service, CancellationToken ct) =>
        GetWorkspaceFeedAsync(workspaceId, context, service, ct, activity: true);

    private static Task<IResult> GetWorkspaceChangesAsync(string workspaceId, HttpContext context, AgentWorkspaceService service, CancellationToken ct) =>
        GetWorkspaceFeedAsync(workspaceId, context, service, ct, activity: false);

    private static async Task<IResult> PostWorkspaceChangeRestoreAsync(
        string workspaceId,
        string changeId,
        HttpContext context,
        AgentWorkspaceService service,
        CancellationToken ct)
    {
        WorkspaceChangeRestoreRequest? request;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct);
            request = text is null
                ? null
                : JsonSerializer.Deserialize<WorkspaceChangeRestoreRequest>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Json(new { error_code = "request_too_large" }, statusCode: 413);
        }
        catch (JsonException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }
        catch (DecoderFallbackException)
        {
            // The bounded body reader decodes strict UTF-8; malformed bytes are a
            // client error, not an unhandled exception (500).
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        if (request is null)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        var result = await service.RestoreWorkspaceChangeAsync(workspaceId, changeId, request, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetWorkspaceFeedAsync(
        string workspaceId,
        HttpContext context,
        AgentWorkspaceService service,
        CancellationToken ct,
        bool activity)
    {
        var afterRaw = context.Request.Query["after_sequence"].ToString();
        var limitRaw = context.Request.Query["limit"].ToString();
        var after = 0L;
        var limit = 50;
        if ((!string.IsNullOrWhiteSpace(afterRaw) && (!long.TryParse(afterRaw, NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0))
            || (!string.IsNullOrWhiteSpace(limitRaw) && (!int.TryParse(limitRaw, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 200)))
        {
            return Results.Json(new { error_code = "invalid_cursor" }, statusCode: 400);
        }

        var result = activity
            ? await service.ListWorkspaceActivityAsync(workspaceId, after, limit, ct)
            : await service.ListWorkspaceChangesAsync(workspaceId, after, limit, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetRunAsync(string runId, AgentRunQueryService service, CancellationToken ct)
    {
        var result = await service.GetRunAsync(runId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetRunActivitiesAsync(string runId, HttpContext context, AgentActivitiesService service, CancellationToken ct)
    {
        // Optional paging: before_sequence reads the page older than that durable sequence and
        // limit bounds its rows. Without either the newest page answers as it always has.
        long? before = null;
        int? limit = null;
        var beforeRaw = context.Request.Query["before_sequence"].ToString();
        if (!string.IsNullOrWhiteSpace(beforeRaw))
        {
            if (!long.TryParse(beforeRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBefore))
            {
                var invalid = AgentV1Shared.BoundedError(400, "invalid_cursor", "before_sequence must be a positive durable sequence.");
                return Results.Json(invalid.Body, statusCode: invalid.StatusCode);
            }

            before = parsedBefore;
        }

        var limitRaw = context.Request.Query["limit"].ToString();
        if (!string.IsNullOrWhiteSpace(limitRaw))
        {
            if (!int.TryParse(limitRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit))
            {
                var invalid = AgentV1Shared.BoundedError(400, "invalid_limit", "limit must be a whole number.");
                return Results.Json(invalid.Body, statusCode: invalid.StatusCode);
            }

            limit = parsedLimit;
        }

        var result = await service.ListRunActivitiesAsync(runId, before, limit, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task GetEventsAsync(string runId, HttpContext context, AgentRunQueryService service, IHarnessEventStore store, HarnessLiveEventHub hub, AgentRunSummaryLivePublisher summaryPublisher, CancellationToken ct)
    {
        // cursor handling: Last-Event-ID header and after_sequence query param
        var afterSequenceRaw = context.Request.Query["after_sequence"].ToString();
        var lastEventIdRaw = context.Request.Headers["Last-Event-ID"].ToString();

        long? afterSeq = null;
        long? lastSeq = null;

        if (!string.IsNullOrWhiteSpace(afterSequenceRaw))
        {
            if (!long.TryParse(afterSequenceRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error_code = "invalid_cursor" }, ct);
                return;
            }

            afterSeq = parsed;
        }

        if (!string.IsNullOrWhiteSpace(lastEventIdRaw))
        {
            // Last-Event-ID may be "id: <seq>" or just seq.  A transient
            // connection-local id (t<seq>) is valid SSE state but never becomes
            // a durable replay cursor.
            var trimmed = lastEventIdRaw.Trim();
            if (trimmed.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[3..].Trim();
            }

            if (trimmed.StartsWith("t", StringComparison.Ordinal))
            {
                if (!long.TryParse(trimmed.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var transientSeq) || transientSeq < 0)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { error_code = "invalid_cursor" }, ct);
                    return;
                }

                lastSeq = null;
            }
            else if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error_code = "invalid_cursor" }, ct);
                return;
            }
            else
            {
                lastSeq = parsed;
            }
        }

        if (afterSeq.HasValue && lastSeq.HasValue && afterSeq.Value != lastSeq.Value)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error_code = "cursor_mismatch" }, ct);
            return;
        }

        var effectiveAfter = afterSeq ?? lastSeq ?? 0;

        // Validate runId format via service get
        var getResult = await service.GetRunAsync(runId, ct);
        if (getResult.StatusCode == 404)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new { error_code = "run_not_found" }, ct);
            return;
        }

        if (getResult.StatusCode == 400)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(getResult.Body, ct);
            return;
        }

        // Validate protocol negotiation before acquiring a long-lived lease or
        // committing SSE headers; invalid requests should still receive a normal
        // JSON error response.
        var protoRaw = context.Request.Query["protocol_version"].ToString();
        if (!string.IsNullOrWhiteSpace(protoRaw)
            && (!int.TryParse(protoRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var proto) || proto != HarnessSafeEventEnvelope.CurrentProtocolVersion))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error_code = "unsupported_protocol_version" }, ct);
            return;
        }

        // Set SSE headers
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        // Need to get run for status mapping
        var runIdObj = new RuAgent.Harness.Core.RunId(runId);
        var run = await store.GetRunAsync(runIdObj, ct);
        if (run is null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new { error_code = "run_not_found" }, ct);
            return;
        }

        context.Response.ContentType = "text/event-stream";

        // Durable cursors stay independent of bounded output batches and transient IDs.
        long lastSent = effectiveAfter;
        long localTransientSeq = 0;
        var summaryWritten = false;
        var isTerminal = run.State.IsTerminal();
        var liveRaw = context.Request.Query["live"].ToString();
        var live = liveRaw != "0";
        HarnessLiveEventHub.HubLease? hubLease = null;
        if (live && !isTerminal)
        {
            // Subscribe before replaying durables. A model step can publish a
            // transient frame while the initial durable cursor is being read;
            // subscribing afterwards creates a silent loss window.
            hubLease = hub.Subscribe(runIdObj);
        }

        // Totals belong to the connection, not to a poll: each poll reads only the events after lastSent.
        var usageTracker = new RunUsageTracker();

        async Task TrackUsageAsync(RuAgent.Harness.Core.HarnessEvent evt)
        {
            if (string.Equals(evt.Kind, "model.usage_recorded", StringComparison.Ordinal)
                && evt.PayloadId is { } usagePayloadId)
            {
                var usage = await service.TryReadModelUsageAsync(runId, usagePayloadId.Value, ct).ConfigureAwait(false);
                usageTracker.Track(usage);
            }
        }

        // A resumed connection replays the usage at or before its cursor so its totals match the run's durable usage.
        async Task SeedUsageTotalsAsync(long cursor)
        {
            long scanned = 0;
            var through = Math.Min(cursor, run.LastSequence);
            while (scanned < through)
            {
                var batch = await HarnessEventReplayBatch.ReadAsync(store, runIdObj, scanned, through, ct);
                if (batch.Count == 0) break;
                foreach (var envelope in batch)
                {
                    await TrackUsageAsync(envelope.Event);
                    scanned = envelope.Event.Sequence;
                }
            }
        }

        async Task StreamDurablesAsync()
        {
            var through = run.LastSequence;
            while (lastSent < through)
            {
                // The reader is disposed before enrichment or any network await.
                var batch = await HarnessEventReplayBatch.ReadAsync(store, runIdObj, lastSent, through, ct);
                if (batch.Count == 0) break;
                foreach (var envelope in batch)
                {
                    var evt = envelope.Event;
                    if (evt.Kind is "model.prompt_composed" or "model.provider_continuation_recorded")
                    {
                        lastSent = evt.Sequence;
                        continue;
                    }
                    await TrackUsageAsync(evt);
                    var safe = HarnessEventMapper.ToSafeEnvelope(evt, run);
                    var (usedInput, usedOutput, usedTotal) = usageTracker.Snapshot();
                    if (usedInput is not null || usedOutput is not null || usedTotal is not null)
                        safe = safe with { InputTokens = usedInput, OutputTokens = usedOutput, TotalTokens = usedTotal };
                    var urgent = evt.Kind is "approval.requested" or "input.requested" or "run.state_changed" or "run.failed";
                    // The writer validates and serializes once. Suppressed frames still
                    // advance this connection's scan cursor without exposing their data.
                    await HarnessSseWriter.WriteDurableAsync(context.Response, safe, ct, flush: urgent);
                    lastSent = evt.Sequence;
                }
                await HarnessSseWriter.FlushAsync(context.Response, ct);
            }
        }

        const int transientBatchSize = 32;
        async Task<int> StreamTransientsAsync(int maximum)
        {
            var sent = 0;
            while (sent < maximum && hubLease is { } lease && lease.TryReadNext(out var frame))
            {
                ct.ThrowIfCancellationRequested();
                localTransientSeq++;
                if (frame.Kind == "agent.summary") summaryWritten = true;
                var payload = new { kind = frame.Kind, data = frame.Data };
                // Text-like deltas flush once per batch; every other frame flushes at once.
                var urgent = frame.Kind is not ("assistant.delta" or "tool.output.delta" or "agent.reasoning_preview");
                await HarnessSseWriter.WriteTransientAsync(context.Response, localTransientSeq, frame.Kind, payload, ct, flush: urgent);
                sent++;
            }
            if (sent > 0) await HarnessSseWriter.FlushAsync(context.Response, ct);
            return sent;
        }

        async Task WriteTerminalSummaryAsync(AgentRun terminalRun)
        {
            if (!terminalRun.State.IsTerminal() || summaryWritten)
            {
                return;
            }

            // Reserve before writing so a runtime publisher racing this endpoint cannot
            // enqueue a duplicate hub frame after the direct connection-local frame.
            await summaryPublisher.TryReserveSummaryAsync(terminalRun, ct).ConfigureAwait(false);
            var summary = summaryPublisher.BuildSummary(terminalRun);
            var wire = new AgentRunSummaryWire(
                summary.Status,
                summary.VerificationState,
                summary.WorkspaceLabel,
                summary.FilesScanned,
                summary.FilesChanged,
                summary.FilesCreated,
                summary.FilesDeleted);
            localTransientSeq++;
            await HarnessSseWriter.WriteTransientAsync(
                    context.Response,
                    localTransientSeq,
                    "agent.summary",
                    new { kind = "agent.summary", data = AgentRunSummaryWireCodec.Serialize(wire) },
                    ct)
                .ConfigureAwait(false);
            summaryWritten = true;
        }

        // One try covers every await from here on. The lease is acquired above,
        // so any failure between the subscribe and the tail loop -- a client
        // disconnect mid-drain, a store error -- used to escape before the
        // finally and leave the subscription in the hub forever.
        try
        {
            if (effectiveAfter > 0)
            {
                await SeedUsageTotalsAsync(effectiveAfter);
            }

            await StreamDurablesAsync();

            // If terminal, send final snapshot and close cleanly
            if (isTerminal)
            {
                // Ensure we have sent up to LastSequence; if not, stream again
                if (lastSent < run.LastSequence)
                {
                    await StreamDurablesAsync();
                }

                await WriteTerminalSummaryAsync(run);
                await HarnessSseWriter.WriteFinalSnapshotAsync(context.Response, AgentV1Service.MapRunState(run.Status), ct);
                await HarnessSseWriter.FlushAsync(context.Response, ct);
                return;
            }

            // Live tail: stream durables and this run's transient frames until the run
            // reaches a terminal state, the client disconnects, or the server shuts down.
            // `live=0` keeps the legacy snapshot-then-close behavior for replay/cursor checks.
            var pollInterval = TimeSpan.FromMilliseconds(100);
            // A run parked on a card changes only when a client answers it, and that POST
            // signals the stream (HarnessLiveEventHub.NotifyRunChanged). The slower poll is
            // the fallback for changes nothing signals, such as a deleted session.
            var parkedPollInterval = TimeSpan.FromSeconds(1);
            if (!live || isTerminal)
            {
                await HarnessSseWriter.FlushAsync(context.Response, ct);
                return;
            }

            var lastStoreCheck = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!ct.IsCancellationRequested && !isTerminal)
            {
                var changed = false;
                var activityPending = false;
                if (hubLease is { } lease)
                {
                    while (lease.ChangedReader.TryRead(out _)) changed = true;
                    activityPending = lease.ActivityReader.TryPeek(out _);
                }
                var interval = IsParkedOnClient(run.State) ? parkedPollInterval : pollInterval;
                var pollDue = System.Diagnostics.Stopwatch.GetElapsedTime(lastStoreCheck) >= interval;
                // Text wakes only drain text. Activity boundaries and explicit run
                // changes reload authority immediately; a fixed clock covers silent commits.
                if (changed || activityPending || pollDue)
                {
                    var reloaded = await store.GetRunAsync(runIdObj, ct);
                    if (reloaded is null)
                    {
                        await HarnessSseWriter.FlushAsync(context.Response, ct);
                        return;
                    }
                    run = reloaded;
                    isTerminal = run.State.IsTerminal();
                    await StreamDurablesAsync();
                    lastStoreCheck = System.Diagnostics.Stopwatch.GetTimestamp();
                }

                if (isTerminal)
                {
                    // Drain a bounded snapshot of the backlog. A late producer must
                    // not keep a completed run's stream open indefinitely.
                    var queued = hubLease?.PendingCount ?? 0;
                    while (queued > 0)
                    {
                        var drained = await StreamTransientsAsync(Math.Min(transientBatchSize, queued));
                        if (drained == 0) break;
                        queued -= drained;
                    }
                    break;
                }
                var sent = await StreamTransientsAsync(transientBatchSize);
                if (sent == transientBatchSize) continue;

                interval = IsParkedOnClient(run.State) ? parkedPollInterval : pollInterval;
                var remaining = interval - System.Diagnostics.Stopwatch.GetElapsedTime(lastStoreCheck);
                if (remaining <= TimeSpan.Zero) continue;
                // Timer APIs truncate sub-millisecond delays; avoid spinning at
                // the very end of an otherwise quiet polling interval.
                var waitDuration = remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining;
                if (hubLease is { } activeLease)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(waitDuration);
                    try
                    {
                        var activity = activeLease.ActivityReader.WaitToReadAsync(wait.Token).AsTask();
                        var delta = activeLease.Reader.WaitToReadAsync(wait.Token).AsTask();
                        var state = activeLease.ChangedReader.WaitToReadAsync(wait.Token).AsTask();
                        await Task.WhenAny(activity, delta, state).ConfigureAwait(false);
                        await wait.CancelAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
                else await Task.Delay(waitDuration, ct);
            }

            await StreamDurablesAsync();
            if (isTerminal)
            {
                await WriteTerminalSummaryAsync(run);
                await HarnessSseWriter.WriteFinalSnapshotAsync(context.Response, AgentV1Service.MapRunState(run.Status), ct);
            }

            await HarnessSseWriter.FlushAsync(context.Response, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // client disconnected
        }
        catch (Exception error)
        {
            // Any other mid-stream failure (store, mapper, writer) must not
            // reset the connection: a TCP RST surfaces on clients as an
            // opaque transport error with no resume cursor. Best-effort flush
            // what was already written and close the response cleanly; the
            // client re-attaches with after_sequence when the run is live.
            // The client sees only a closed stream, so the cause is logged here.
            context.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger("RuBackend.Harness.AgentEventStream")
                .LogWarning(error, "agent event stream for run {RunId} closed after a server-side failure", runId);
            try
            {
                await HarnessSseWriter.FlushAsync(context.Response, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Client already gone; nothing left to salvage.
            }
        }
        finally
        {
            if (hubLease is { } activeLease)
            {
                await activeLease.DisposeAsync();
            }
        }
    }

    /// <summary>States that only a client request (a POST to the run) can move on.</summary>
    private static bool IsParkedOnClient(HarnessRunState state) =>
        state is HarnessRunState.WaitingWorkspaceTrust
            or HarnessRunState.WaitingApproval
            or HarnessRunState.WaitingUser
            or HarnessRunState.ReconciliationRequired;

    private static async Task<IResult> GetPayloadAsync(string runId, string payloadId, AgentRunQueryService service, CancellationToken ct)
    {
        var result = await service.GetPayloadAsync(runId, payloadId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostApprovalAsync(string runId, string approvalId, HttpContext context, AgentApprovalsService service, CancellationToken ct)
    {
        System.Text.Json.JsonElement body = default;
        bool hasBody = false;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var document = System.Text.Json.JsonDocument.Parse(text);
                body = document.RootElement.Clone();
                hasBody = true;
            }
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Json(new { error_code = "request_too_large" }, statusCode: 413);
        }
        catch (JsonException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }
        catch (DecoderFallbackException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        var result = await service.HandleApprovalAsync(runId, approvalId, hasBody ? body : null, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostInputAsync(string runId, string inputId, HttpContext context, AgentInputsService service, CancellationToken ct)
    {
        System.Text.Json.JsonElement body = default;
        bool hasBody = false;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var document = System.Text.Json.JsonDocument.Parse(text);
                body = document.RootElement.Clone();
                hasBody = true;
            }
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Json(new { error_code = "request_too_large" }, statusCode: 413);
        }
        catch (JsonException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }
        catch (DecoderFallbackException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        var result = await service.HandleInputAsync(runId, inputId, hasBody ? body : null, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetSteersAsync(string runId, AgentSteersService service, CancellationToken ct)
    {
        var result = await service.ListAsync(runId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostSteerAsync(string runId, HttpContext context, AgentSteersService service, CancellationToken ct)
    {
        System.Text.Json.JsonElement? body = null;
        try
        {
            // A steer carries what a new run's prompt can: long text and its images.
            var text = await ReadBoundedBodyTextAsync(context.Request, ct, MaxJsonBodyBytesAgentRun);
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var document = System.Text.Json.JsonDocument.Parse(text);
                body = document.RootElement.Clone();
            }
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Json(new { error_code = "request_too_large" }, statusCode: 413);
        }
        catch (JsonException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }
        catch (DecoderFallbackException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        var result = await service.SteerAsync(runId, body, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostTrustAsync(string runId, HttpContext context, AgentWorkspaceTrustService service, CancellationToken ct)
    {
        TrustAgentRunRequest? dto;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct);
            dto = text is null
                ? null
                : JsonSerializer.Deserialize<TrustAgentRunRequest>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Json(new { error_code = "request_too_large" }, statusCode: 413);
        }
        catch (JsonException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }
        catch (DecoderFallbackException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        if (dto is null)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        var result = await service.TrustAsync(runId, dto, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostCancelAsync(string runId, AgentRunLifecycleService service, CancellationToken ct)
    {
        var result = await service.CancelAsync(runId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostCompactAsync(
        string runId,
        HttpContext context,
        AgentCommandsService service,
        CancellationToken ct)
    {
        CompactAgentRunRequest? dto;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                dto = new CompactAgentRunRequest();
            }
            else
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || document.RootElement.EnumerateObject().Any(property =>
                        !string.Equals(property.Name, "strategy", StringComparison.Ordinal)))
                {
                    return Results.Json(
                        new { error_code = "invalid_request" },
                        statusCode: 400);
                }

                var strategy = document.RootElement.TryGetProperty("strategy", out var value)
                    ? value.ValueKind == JsonValueKind.Null
                        ? null
                        : value.ValueKind == JsonValueKind.String
                            ? value.GetString()
                            : throw new JsonException("strategy must be a string")
                    : null;
                dto = new CompactAgentRunRequest(strategy);
            }
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Json(new { error_code = "request_too_large" }, statusCode: 413);
        }
        catch (JsonException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }
        catch (DecoderFallbackException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        var result = await service.CompactAsync(runId, dto, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostCommandAsync(
        string runId,
        HttpContext context,
        AgentCommandsService service,
        CancellationToken ct)
    {
        DispatchAgentCommandRequest? dto;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
            }

            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Any(property =>
                    !string.Equals(property.Name, "text", StringComparison.Ordinal)))
            {
                return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
            }

            if (!document.RootElement.TryGetProperty("text", out var commandText)
                || commandText.ValueKind != JsonValueKind.String
                || commandText.GetString() is not { } value)
            {
                return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
            }

            dto = new DispatchAgentCommandRequest(value);
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Json(new { error_code = "request_too_large" }, statusCode: 413);
        }
        catch (JsonException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }
        catch (DecoderFallbackException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        var result = await service.DispatchCommandAsync(runId, dto, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetSessionRunsAsync(string sessionId, HttpContext context, AgentRunQueryService service, CancellationToken ct)
    {
        var limitRaw = context.Request.Query["limit"].ToString();
        int? limit = null;
        if (!string.IsNullOrWhiteSpace(limitRaw))
        {
            if (!int.TryParse(limitRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return Results.Json(new { error_code = "invalid_limit" }, statusCode: 400);
            }

            limit = parsed;
        }

        var cursor = context.Request.Query["cursor"].ToString();
        if (string.IsNullOrWhiteSpace(cursor))
        {
            cursor = null;
        }

        var result = await service.ListSessionRunsAsync(sessionId, limit, cursor, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetSessionPresetAsync(string sessionId, HttpContext context, AgentPoliciesService service, CancellationToken ct)
    {
        var result = await service.GetSessionPresetAsync(sessionId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetPoliciesAsync(HttpContext context, AgentPoliciesService service, CancellationToken ct)
    {
        var scope = context.Request.Query["scope"].ToString();
        if (string.IsNullOrWhiteSpace(scope)) scope = null;
        var workspaceId = context.Request.Query["workspace_id"].ToString();
        if (string.IsNullOrWhiteSpace(workspaceId)) workspaceId = null;
        var result = await service.ListPoliciesAsync(scope, workspaceId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostPolicyAsync(HttpContext context, AgentPoliciesService service, CancellationToken ct)
    {
        bool isInteractive = string.Equals(context.Request.Headers["X-Interactive-Confirmed"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        JsonElement body;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
            }

            using var document = JsonDocument.Parse(text);
            body = document.RootElement.Clone();
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Json(new { error_code = "request_too_large" }, statusCode: 413);
        }
        catch (JsonException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }
        catch (DecoderFallbackException)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        try
        {
            var result = await service.CreatePolicyAsync(body, isInteractive, ct);
            return Results.Json(result.Body, statusCode: result.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // The client sees only the bounded code, so the cause is logged here.
            context.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger("RuBackend.Harness.AgentPolicies")
                .LogError(error, "policy creation failed");
            return Results.Json(new { error_code = "policy_create_failed", message = "Unable to create policy." }, statusCode: 500);
        }
    }

    private static async Task<IResult> DeletePolicyAsync(string ruleId, HttpContext context, AgentPoliciesService service, CancellationToken ct)
    {
        bool isInteractive = string.Equals(context.Request.Headers["X-Interactive-Confirmed"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var result = await service.DeletePolicyAsync(ruleId, isInteractive, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<string?> ReadBoundedBodyTextAsync(HttpRequest request, CancellationToken cancellationToken, int maxBodyBytes = MaxJsonBodyBytesDefault)
    {
        var (read, text) = await BoundedRequestBodyReader
            .ReadTextAsync(request, maxBodyBytes, cancellationToken)
            .ConfigureAwait(false);
        if (!read)
        {
            throw new RequestBodyTooLargeException();
        }

        return text;
    }

    private sealed class RequestBodyTooLargeException : Exception;
}

internal static class HarnessEventMapper
{
    public static string ToSafeSummary(RuAgent.Harness.Core.HarnessEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.SafeCode))
        {
            return evt.SafeCode!;
        }

        if (evt.SafeCount is not null)
        {
            return evt.SafeCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return evt.Kind;
    }

    public static string MapStatus(RuAgent.Harness.Core.AgentRun run) =>
        RuBackend.Harness.AgentV1Service.MapRunState(run.Status);

    public static RuAgent.Protocol.Harness.HarnessSafeEventEnvelope ToSafeEnvelope(RuAgent.Harness.Core.HarnessEvent evt, RuAgent.Harness.Core.AgentRun run)
    {
        var payloadRef = evt.PayloadId?.Value;
        return new RuAgent.Protocol.Harness.HarnessSafeEventEnvelope(
            ProtocolVersion: RuAgent.Protocol.Harness.HarnessSafeEventEnvelope.CurrentProtocolVersion,
            EventId: evt.EventId.Value,
            RunId: evt.RunId.Value,
            SessionId: run.SessionId.Value,
            Sequence: evt.Sequence,
            EventType: evt.Kind,
            OccurredAt: evt.OccurredAt.ToUniversalTime().ToString("O"),
            Status: string.Equals(evt.Kind, "input.requested", StringComparison.Ordinal)
                ? "waiting_input"
                : MapStatus(run),
            SafeSummary: ToSafeSummary(evt),
            PayloadRef: payloadRef,
            ApprovalId: evt.ApprovalId?.Value,
            ModelStepId: evt.ModelStepId?.Value);
    }
}
