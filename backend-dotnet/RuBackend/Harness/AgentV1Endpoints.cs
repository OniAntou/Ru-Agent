using System.Text.Json;
using System.Globalization;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;
using RuBackend.Security;

namespace RuBackend.Harness;

public static class AgentV1Endpoints
{
    private const int MaxJsonBodyBytes = 64 * 1024;
    private const int MaxJsonBodyBytesDefault = MaxJsonBodyBytes;

    // Four data URLs at the per-image character cap (5,600,000) plus the JSON
    // envelope. The service-level 4MB/20MB image caps are only reachable when the
    // transport cap is at least this large.
    private const int MaxJsonBodyBytesAgentRun = 24 * 1024 * 1024;

    public static void Map(WebApplication app)
    {
        app.MapPost("/agent/v1/runs", CreateRunAsync);
        app.MapGet("/agent/v1/runs/{runId}", GetRunAsync);
        app.MapGet("/agent/v1/runs/{runId}/events", GetEventsAsync);
        app.MapGet("/agent/v1/runs/{runId}/activities", GetRunActivitiesAsync);
        app.MapGet("/agent/v1/runs/{runId}/payloads/{payloadId}", GetPayloadAsync);
        app.MapPost("/agent/v1/runs/{runId}/approvals/{approvalId}", PostApprovalAsync);
        app.MapPost("/agent/v1/runs/{runId}/inputs/{inputId}", PostInputAsync);
        app.MapPost("/agent/v1/runs/{runId}/trust", PostTrustAsync);
        app.MapPost("/agent/v1/runs/{runId}/cancel", PostCancelAsync);
        app.MapPost("/agent/v1/runs/{runId}/continue", PostContinueAsync);
        app.MapPost("/agent/v1/runs/{runId}/compact", PostCompactAsync);
        app.MapPost("/agent/v1/runs/{runId}/commands", PostCommandAsync);
        app.MapPost("/agent/v1/runs/{runId}/reconcile", PostReconcileAsync);
        app.MapGet("/agent/v1/sessions/{sessionId}/runs", GetSessionRunsAsync);
        app.MapGet("/agent/v1/policies", GetPoliciesAsync);
        app.MapPost("/agent/v1/policies", PostPolicyAsync);
        app.MapDelete("/agent/v1/policies/{ruleId}", DeletePolicyAsync);
        app.MapGet("/agent/v1/workspaces/{workspaceId}/concurrency", GetWorkspaceConcurrencyAsync);
        app.MapPut("/agent/v1/workspaces/{workspaceId}/concurrency", PutWorkspaceConcurrencyAsync);
        app.MapGet("/agent/v1/workspaces/{workspaceId}/activity", GetWorkspaceActivityAsync);
        app.MapGet("/agent/v1/workspaces/{workspaceId}/changes", GetWorkspaceChangesAsync);
        app.MapPost("/agent/v1/workspaces/{workspaceId}/changes/{changeId}/restore", PostWorkspaceChangeRestoreAsync);
    }

    private static async Task<IResult> CreateRunAsync(HttpContext context, AgentV1Service service, CancellationToken ct)
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

        var result = await service.CreateRunAsync(dto, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetWorkspaceConcurrencyAsync(string workspaceId, AgentV1Service service, CancellationToken ct)
    {
        var result = await service.GetWorkspaceConcurrencyAsync(workspaceId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PutWorkspaceConcurrencyAsync(string workspaceId, HttpContext context, AgentV1Service service, CancellationToken ct)
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

        if (request is null)
        {
            return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
        }

        var result = await service.UpdateWorkspaceConcurrencyAsync(workspaceId, request, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static Task<IResult> GetWorkspaceActivityAsync(string workspaceId, HttpContext context, AgentV1Service service, CancellationToken ct) =>
        GetWorkspaceFeedAsync(workspaceId, context, service, ct, activity: true);

    private static Task<IResult> GetWorkspaceChangesAsync(string workspaceId, HttpContext context, AgentV1Service service, CancellationToken ct) =>
        GetWorkspaceFeedAsync(workspaceId, context, service, ct, activity: false);

    private static async Task<IResult> PostWorkspaceChangeRestoreAsync(
        string workspaceId,
        string changeId,
        HttpContext context,
        AgentV1Service service,
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
        AgentV1Service service,
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

    private static async Task<IResult> GetRunAsync(string runId, AgentV1Service service, CancellationToken ct)
    {
        var result = await service.GetRunAsync(runId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetRunActivitiesAsync(string runId, AgentV1Service service, CancellationToken ct)
    {
        var result = await service.ListRunActivitiesAsync(runId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task GetEventsAsync(string runId, HttpContext context, AgentV1Service service, IHarnessEventStore store, HarnessLiveEventHub hub, AgentRunSummaryLivePublisher summaryPublisher, CancellationToken ct)
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

        // Streaming durable events without materializing
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
            await foreach (var envelope in store.ReadEventsAsync(runIdObj, 0, ct))
            {
                if (envelope.Event.Sequence > cursor)
                {
                    break;
                }

                await TrackUsageAsync(envelope.Event);
            }
        }

        // Helper to stream available durables
        async Task<bool> StreamDurablesAsync()
        {
            var any = false;
            await foreach (var envelope in store.ReadEventsAsync(runIdObj, lastSent, ct))
            {
                any = true;
                // Prompt receipts and provider continuations are durable host-internal evidence. Advance the
                // durable cursor without mapping or exposing their payload reference;
                // reconnecting clients must not replay the suppressed event.
                if (envelope.Event.Kind is "model.prompt_composed" or "model.provider_continuation_recorded")
                {
                    lastSent = envelope.Event.Sequence;
                    continue;
                }

                // Cumulative usage enrich: resolve ModelUsage payloads once per
                // request and attach running totals to following envelopes.
                await TrackUsageAsync(envelope.Event);

                var safe = HarnessEventMapper.ToSafeEnvelope(envelope.Event, run);
                var (usedInput, usedOutput, usedTotal) = usageTracker.Snapshot();
                if (usedInput is not null || usedOutput is not null || usedTotal is not null)
                {
                    safe = safe with { InputTokens = usedInput, OutputTokens = usedOutput, TotalTokens = usedTotal };
                }

                // Verify safe envelope not containing forbidden fields
                var json = JsonSerializer.Serialize(safe);
                if (HarnessEventContractValidator.ContainsForbiddenField(json))
                {
                    // Should never happen, but drop
                    continue;
                }

                await HarnessSseWriter.WriteDurableAsync(context.Response, safe, ct);
                lastSent = envelope.Event.Sequence;
                // Update run snapshot if state changed? Refresh run after each event maybe
                // For simplicity keep original run for status, but lastSent advances.
            }

            return any;
        }

        async Task WriteTerminalSummaryAsync(AgentRun terminalRun)
        {
            if (!terminalRun.State.IsTerminal() || summaryWritten)
            {
                return;
            }

            // Reserve before writing so a runtime publisher racing this endpoint cannot
            // enqueue a duplicate hub frame after the direct connection-local frame.
            summaryPublisher.TryReserveSummary(terminalRun, out _);
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
            await context.Response.Body.FlushAsync(ct);
            return;
        }

        // Live tail: stream durables and this run's transient frames until the run
        // reaches a terminal state, the client disconnects, or the server shuts down.
        // `live=0` keeps the legacy snapshot-then-close behavior for replay/cursor checks.
        var pollInterval = TimeSpan.FromMilliseconds(100);
        if (!live || isTerminal)
        {
            await context.Response.Body.FlushAsync(ct);
            return;
        }

                while (!ct.IsCancellationRequested && !isTerminal)
                {
                    var reloadedRun = await store.GetRunAsync(runIdObj, ct);
                    if (reloadedRun is not null)
                    {
                        run = reloadedRun;
                        isTerminal = run.State.IsTerminal();
                    }

                    await StreamDurablesAsync();

                    // Activity rows ride a dedicated hub channel so a delta
                    // burst can never evict them. Drain them first: a slow
                    // client must see the tool row even when it later drops
                    // some of the surrounding streamed text.
                    while (hubLease is { } activityLease && activityLease.ActivityReader.TryRead(out var activityFrame))
                    {
                        localTransientSeq++;
                        if (activityFrame.Kind == "agent.summary")
                        {
                            summaryWritten = true;
                        }
                        var activityPayload = new { kind = activityFrame.Kind, data = activityFrame.Data };
                        await HarnessSseWriter.WriteTransientAsync(context.Response, localTransientSeq, activityFrame.Kind, activityPayload, ct);
                        // transient does not advance lastSent (durable cursor)
                    }

                    while (hubLease is { } activeLease && activeLease.Reader.TryRead(out var frame))
                    {
                        localTransientSeq++;
                        if (frame.Kind == "agent.summary")
                        {
                            summaryWritten = true;
                        }
                        var payload = new { kind = frame.Kind, data = frame.Data };
                        await HarnessSseWriter.WriteTransientAsync(context.Response, localTransientSeq, frame.Kind, payload, ct);
                        // transient does not advance lastSent (durable cursor)
                    }

                    if (isTerminal)
                    {
                        break;
                    }

                    if (!ct.IsCancellationRequested && !isTerminal)
                    {
                        // Wait for the next transient frame, but never indefinitely: a
                        // durable event can be committed while the run has nothing live to
                        // publish (the approval request parks the run in WaitingApproval
                        // right after the last activity frame), and the client must still
                        // receive it. Bound the wait by the poll interval and re-check the
                        // durable store when it elapses.
                        if (hubLease is { } activeLease)
                        {
                            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            pollCts.CancelAfter(pollInterval);
                            try
                            {
                                var activityWait = activeLease.ActivityReader.WaitToReadAsync(pollCts.Token).AsTask();
                                var deltaWait = activeLease.Reader.WaitToReadAsync(pollCts.Token).AsTask();
                                await Task.WhenAny(activityWait, deltaWait).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                // Poll interval elapsed with no live frame; loop to flush durables.
                            }
                        }
                        else
                        {
                            await Task.Delay(pollInterval, ct);
                        }
                    }
                }

                await StreamDurablesAsync();
                if (isTerminal)
                {
                    await WriteTerminalSummaryAsync(run);
                    await HarnessSseWriter.WriteFinalSnapshotAsync(context.Response, AgentV1Service.MapRunState(run.Status), ct);
                }

                await context.Response.Body.FlushAsync(ct);
            }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // client disconnected
        }
        catch (Exception)
        {
            // Any other mid-stream failure (store, mapper, writer) must not
            // reset the connection: a TCP RST surfaces on clients as an
            // opaque transport error with no resume cursor. Best-effort flush
            // what was already written and close the response cleanly; the
            // client re-attaches with after_sequence when the run is live.
            try
            {
                await context.Response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);
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

    private static async Task<IResult> GetPayloadAsync(string runId, string payloadId, AgentV1Service service, CancellationToken ct)
    {
        var result = await service.GetPayloadAsync(runId, payloadId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostApprovalAsync(string runId, string approvalId, HttpContext context, AgentV1Service service, CancellationToken ct)
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

    private static async Task<IResult> PostInputAsync(string runId, string inputId, HttpContext context, AgentV1Service service, CancellationToken ct)
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

    private static async Task<IResult> PostTrustAsync(string runId, HttpContext context, AgentV1Service service, CancellationToken ct)
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

    private static async Task<IResult> PostCancelAsync(string runId, AgentV1Service service, CancellationToken ct)
    {
        var result = await service.CancelAsync(runId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostContinueAsync(string runId, AgentV1Service service, CancellationToken ct)
    {
        var result = await service.ContinueAsync(runId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostCompactAsync(
        string runId,
        HttpContext context,
        AgentV1Service service,
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
        AgentV1Service service,
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

    private static async Task<IResult> PostReconcileAsync(string runId, HttpContext context, AgentV1Service service, CancellationToken ct)
    {
        string? outcome = null;
        string? snapshotHash = null;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
            }

            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("outcome", out var outcomeElement)
                    && outcomeElement.ValueKind == JsonValueKind.String)
                {
                    outcome = outcomeElement.GetString();
                }

                if (document.RootElement.TryGetProperty("snapshot_hash", out var hashElement)
                    && hashElement.ValueKind == JsonValueKind.String)
                {
                    snapshotHash = hashElement.GetString();
                }
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

        var result = await service.ReconcileAsync(runId, outcome, snapshotHash, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetSessionRunsAsync(string sessionId, HttpContext context, AgentV1Service service, CancellationToken ct)
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

    private static async Task<IResult> GetPoliciesAsync(HttpContext context, AgentV1Service service, CancellationToken ct)
    {
        var scope = context.Request.Query["scope"].ToString();
        if (string.IsNullOrWhiteSpace(scope)) scope = null;
        var workspaceId = context.Request.Query["workspace_id"].ToString();
        if (string.IsNullOrWhiteSpace(workspaceId)) workspaceId = null;
        var result = await service.ListPoliciesAsync(scope, workspaceId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostPolicyAsync(HttpContext context, AgentV1Service service, CancellationToken ct)
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
        catch (Exception)
        {
            return Results.Json(new { error_code = "policy_create_failed", message = "Unable to create policy." }, statusCode: 500);
        }
    }

    private static async Task<IResult> DeletePolicyAsync(string ruleId, HttpContext context, AgentV1Service service, CancellationToken ct)
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
            ApprovalId: evt.ApprovalId?.Value);
    }
}
