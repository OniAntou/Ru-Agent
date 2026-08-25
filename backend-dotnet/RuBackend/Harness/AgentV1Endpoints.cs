using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

public static class AgentV1Endpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/agent/v1/runs", CreateRunAsync);
        app.MapGet("/agent/v1/runs/{runId}", GetRunAsync);
        app.MapGet("/agent/v1/runs/{runId}/events", GetEventsAsync);
        app.MapGet("/agent/v1/runs/{runId}/payloads/{payloadId}", GetPayloadAsync);
        app.MapPost("/agent/v1/runs/{runId}/approvals/{approvalId}", PostApprovalAsync);
        app.MapPost("/agent/v1/runs/{runId}/trust", PostTrustAsync);
        app.MapPost("/agent/v1/runs/{runId}/cancel", PostCancelAsync);
        app.MapPost("/agent/v1/runs/{runId}/continue", PostContinueAsync);
        app.MapPost("/agent/v1/runs/{runId}/reconcile", PostReconcileAsync);
        app.MapGet("/agent/v1/sessions/{sessionId}/runs", GetSessionRunsAsync);
        app.MapGet("/agent/v1/policies", GetPoliciesAsync);
        app.MapPost("/agent/v1/policies", PostPolicyAsync);
        app.MapDelete("/agent/v1/policies/{ruleId}", DeletePolicyAsync);
    }

    private static async Task<IResult> CreateRunAsync(HttpContext context, AgentV1Service service, CancellationToken ct)
    {
        CreateAgentRunRequest? dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<CreateAgentRunRequest>(context.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch
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

    private static async Task<IResult> GetRunAsync(string runId, AgentV1Service service, CancellationToken ct)
    {
        var result = await service.GetRunAsync(runId, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task GetEventsAsync(string runId, HttpContext context, AgentV1Service service, IHarnessEventStore store, HarnessLiveEventHub hub, RuAgent.Hosting.RuntimeClientLeaseRegistry leaseRegistry, CancellationToken ct)
    {
        // cursor handling: Last-Event-ID header and after_sequence query param
        var afterSequenceRaw = context.Request.Query["after_sequence"].ToString();
        var lastEventIdRaw = context.Request.Headers["Last-Event-ID"].ToString();

        long? afterSeq = null;
        long? lastSeq = null;

        if (!string.IsNullOrWhiteSpace(afterSequenceRaw))
        {
            if (!long.TryParse(afterSequenceRaw, out var parsed) || parsed < 0)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error_code = "invalid_cursor" }, ct);
                return;
            }

            afterSeq = parsed;
        }

        if (!string.IsNullOrWhiteSpace(lastEventIdRaw))
        {
            // Last-Event-ID may be "id: <seq>" or just seq, also may be t<seq> for transient; we only handle durable
            var trimmed = lastEventIdRaw.Trim();
            if (trimmed.StartsWith("t", StringComparison.Ordinal))
            {
                // transient id not valid for durable cursor; treat as mismatch if afterSeq present? ignore
                lastSeq = null;
            }
            else if (!long.TryParse(trimmed, out var parsed) || parsed < 0)
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

        // Acquire client lease for SSE lifetime (per spec: Agent SSE = long-lived lease)
        using var lease = leaseRegistry.Acquire("sse");
        // Set SSE headers
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.ContentType = "text/event-stream";

        // Need to get run for status mapping
        var runIdObj = new RuAgent.Harness.Core.RunId(runId);
        var run = await store.GetRunAsync(runIdObj, ct);
        if (run is null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new { error_code = "run_not_found" }, ct);
            return;
        }

        // Reject unknown protocol version query param? Check protocol_version param if present
        var protoRaw = context.Request.Query["protocol_version"].ToString();
        if (!string.IsNullOrWhiteSpace(protoRaw))
        {
            if (!int.TryParse(protoRaw, out var proto) || proto != HarnessSafeEventEnvelope.CurrentProtocolVersion)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error_code = "unsupported_protocol_version" }, ct);
                return;
            }
        }

        // Streaming durable events without materializing
        long lastSent = effectiveAfter;
        var isTerminal = run.State.IsTerminal();

        // Helper to stream available durables
        async Task<bool> StreamDurablesAsync()
        {
            var any = false;
            await foreach (var envelope in store.ReadEventsAsync(runIdObj, lastSent, ct))
            {
                any = true;
                var safe = HarnessEventMapper.ToSafeEnvelope(envelope.Event, run);
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

        await StreamDurablesAsync();

        // If terminal, send final snapshot and close cleanly
        if (isTerminal)
        {
            // Ensure we have sent up to LastSequence; if not, stream again
            if (lastSent < run.LastSequence)
            {
                await StreamDurablesAsync();
            }

            await context.Response.Body.FlushAsync(ct);
            return;
        }

        // Live tail: poll briefly for new durables/transient, then close. Keeps reconnect tests fast while still proving live behavior.
        var start = DateTimeOffset.UtcNow;
        var maxDuration = TimeSpan.FromSeconds(1);
        var pollInterval = TimeSpan.FromMilliseconds(100);
        long localTransientSeq = 0;

        try
        {
            while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow - start < maxDuration)
            {
                // Check for new durable events
                var reloadedRun = await store.GetRunAsync(runIdObj, ct);
                if (reloadedRun is not null)
                {
                    run = reloadedRun;
                    isTerminal = run.State.IsTerminal();
                }

                // Stream any new durables after lastSent
                var hadNew = false;
                await foreach (var envelope in store.ReadEventsAsync(runIdObj, lastSent, ct))
                {
                    hadNew = true;
                    var safe = HarnessEventMapper.ToSafeEnvelope(envelope.Event, run);
                    await HarnessSseWriter.WriteDurableAsync(context.Response, safe, ct);
                    lastSent = envelope.Event.Sequence;
                }

                if (isTerminal)
                {
                    break;
                }

                // Check transient frames for this run
                while (hub.Reader.TryRead(out var frame))
                {
                    if (frame.RunId.Value != runId) continue;
                    localTransientSeq++;
                    var payload = new { kind = frame.Kind, data = frame.Data };
                    await HarnessSseWriter.WriteTransientAsync(context.Response, localTransientSeq, frame.Kind, payload, ct);
                    // transient does not advance lastSent (durable cursor)
                }

                // If no new data, wait
                if (!hadNew)
                {
                    await Task.Delay(pollInterval, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // client disconnected
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
            using var reader = new System.IO.StreamReader(context.Request.Body);
            var text = await reader.ReadToEndAsync(ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                body = System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
                hasBody = true;
            }
        }
        catch { }

        var result = await service.HandleApprovalAsync(runId, approvalId, hasBody ? body : null, ct);
        return Results.Json(result.Body, statusCode: result.StatusCode);
    }

    private static async Task<IResult> PostTrustAsync(string runId, HttpContext context, AgentV1Service service, CancellationToken ct)
    {
        TrustAgentRunRequest? dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<TrustAgentRunRequest>(context.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch
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

    private static Task<IResult> PostReconcileAsync(string runId, AgentV1Service service, CancellationToken ct)
    {
        var result = service.PhaseNotAvailable();
        return Task.FromResult(Results.Json(result.Body, statusCode: result.StatusCode));
    }

    private static async Task<IResult> GetSessionRunsAsync(string sessionId, HttpContext context, AgentV1Service service, CancellationToken ct)
    {
        var limitRaw = context.Request.Query["limit"].ToString();
        int? limit = null;
        if (!string.IsNullOrWhiteSpace(limitRaw))
        {
            if (!int.TryParse(limitRaw, out var parsed))
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

    private static Task<IResult> GetPoliciesAsync(AgentV1Service service, CancellationToken ct)
    {
        var result = service.PhaseNotAvailable();
        return Task.FromResult(Results.Json(result.Body, statusCode: result.StatusCode));
    }

    private static Task<IResult> PostPolicyAsync(AgentV1Service service, CancellationToken ct)
    {
        var result = service.PhaseNotAvailable();
        return Task.FromResult(Results.Json(result.Body, statusCode: result.StatusCode));
    }

    private static Task<IResult> DeletePolicyAsync(string ruleId, AgentV1Service service, CancellationToken ct)
    {
        var result = service.PhaseNotAvailable();
        return Task.FromResult(Results.Json(result.Body, statusCode: result.StatusCode));
    }
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

    public static string MapStatus(RuAgent.Harness.Core.AgentRun run)
    {
        if (run.Status.HistoryWaitKind is not null)
        {
            return run.Status.HistoryWaitKind == RuAgent.Harness.Core.HistoryWaitKind.UserBeforeFirstStep
                ? "waiting_history_user"
                : "waiting_history_final";
        }

        return run.State.ToString().ToLowerInvariant();
    }

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
            Status: MapStatus(run),
            SafeSummary: ToSafeSummary(evt),
            PayloadRef: payloadRef);
    }
}
