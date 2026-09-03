using System.Text.Json;
using System.Globalization;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

public static class AgentV1Endpoints
{
    private const int MaxJsonBodyBytes = 64 * 1024;
    private const int MaxJsonBodyBytesDefault = MaxJsonBodyBytes;
    private const int MaxJsonBodyBytesAgentRun = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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

        // Helper to stream available durables
        async Task<bool> StreamDurablesAsync()
        {
            var any = false;
            await foreach (var envelope in store.ReadEventsAsync(runIdObj, lastSent, ct))
            {
                any = true;
                // Prompt receipts are durable host-internal evidence. Advance the
                // durable cursor without mapping or exposing their payload reference;
                // reconnecting clients must not replay the suppressed event.
                if (string.Equals(
                        envelope.Event.Kind,
                        "model.prompt_composed",
                        StringComparison.Ordinal))
                {
                    lastSent = envelope.Event.Sequence;
                    continue;
                }

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
            if (hubLease is { } terminalLease)
            {
                await terminalLease.DisposeAsync();
            }
            return;
        }

        // Live tail: stream durables and this run's transient frames until the run
        // reaches a terminal state, the client disconnects, or the server shuts down.
        // `live=0` keeps the legacy snapshot-then-close behavior for replay/cursor checks.
        var pollInterval = TimeSpan.FromMilliseconds(100);
        if (!live || isTerminal)
        {
            await context.Response.Body.FlushAsync(ct);
            if (hubLease is { } replayLease)
            {
                await replayLease.DisposeAsync();
            }
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested && !isTerminal)
            {
                var reloadedRun = await store.GetRunAsync(runIdObj, ct);
                if (reloadedRun is not null)
                {
                    run = reloadedRun;
                    isTerminal = run.State.IsTerminal();
                }

                await StreamDurablesAsync();

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
                    await Task.Delay(pollInterval, ct);
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
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentLength is > 0 && request.ContentLength > maxBodyBytes)
        {
            throw new RequestBodyTooLargeException();
        }

        using var buffer = new MemoryStream(
            request.ContentLength is >= 0 and <= int.MaxValue && request.ContentLength <= maxBodyBytes
                ? checked((int)request.ContentLength.Value)
                : 0);
        var chunk = new byte[8 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length > maxBodyBytes - read)
            {
                throw new RequestBodyTooLargeException();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (buffer.Length == 0)
        {
            return null;
        }

        var text = StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
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
            Status: MapStatus(run),
            SafeSummary: ToSafeSummary(evt),
            PayloadRef: payloadRef,
            ApprovalId: evt.ApprovalId?.Value);
    }
}
