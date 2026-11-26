using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Steering;
using RuAgent.Harness.Persistence;
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

/// <summary>
/// Steers sent into a working root run (ADR 0056). The Host stores the steer and its images
/// durably and answers at once; the run's coordinator delivers them at the next step boundary. A steer is
/// a user prompt, so the user's prompt hooks see it first, under the same trust and preset
/// rule as a new run's prompt.
/// </summary>
public sealed class AgentSteersService
{
    private readonly IHarnessEventStore _store;
    private readonly SqliteRunSteerStore _steers;
    private readonly ITimeProvider _time;
    private readonly AgentV1SubagentGuard _subagentGuard;
    private readonly IAgentRuntimeRootRegistry _runtimeRoots;
    private readonly Hooks.RunLifecycleHooks? _lifecycleHooks;

    public AgentSteersService(
        IHarnessEventStore store,
        SqliteRunSteerStore steers,
        ITimeProvider time,
        AgentV1SubagentGuard subagentGuard,
        IAgentRuntimeRootRegistry runtimeRoots,
        Hooks.RunLifecycleHooks? lifecycleHooks = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _steers = steers ?? throw new ArgumentNullException(nameof(steers));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _subagentGuard = subagentGuard ?? throw new ArgumentNullException(nameof(subagentGuard));
        _runtimeRoots = runtimeRoots ?? throw new ArgumentNullException(nameof(runtimeRoots));
        _lifecycleHooks = lifecycleHooks;
    }

    public async Task<AgentServiceResult> SteerAsync(string runIdRaw, JsonElement? body, CancellationToken ct)
    {
        if (!IsRunId(runIdRaw)) return BoundedError(400, "invalid_run_id", "run_id invalid.");
        var runId = new RunId(runIdRaw);
        if (await _subagentGuard.IsInternalRunAsync(runId, ct).ConfigureAwait(false))
            return BoundedError(409, "subagent_read_only", "Subagents are controlled by the main agent. Send your request to the parent.");

        string? steerId = null, text = null;
        var images = new List<string>();
        if (body is { ValueKind: JsonValueKind.Object } value)
        {
            if (value.TryGetProperty("steer_id", out var idNode) && idNode.ValueKind == JsonValueKind.String)
                steerId = idNode.GetString();
            if (value.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String)
                text = textNode.GetString();
            if (value.TryGetProperty("images", out var imagesNode) && imagesNode.ValueKind != JsonValueKind.Null)
            {
                if (imagesNode.ValueKind != JsonValueKind.Array
                    || imagesNode.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                    return BoundedError(400, "invalid_image_mime", "images must be an array of image data URLs.");
                images.AddRange(imagesNode.EnumerateArray().Select(item => item.GetString()!));
            }
        }

        if (!RunSteerContract.IsValidId(steerId))
            return BoundedError(400, "invalid_steer_id", "steer_id must be 'steer_' followed by 32 lowercase hexadecimal characters.");
        // Images may carry a steer on their own, as they may a new run's prompt.
        text ??= string.Empty;
        if (string.IsNullOrWhiteSpace(text) && images.Count == 0
            || text.Length > RunSteerContract.MaxCharacters || text.Contains('\0'))
            return BoundedError(400, "invalid_steer_text", $"text must be 1..{RunSteerContract.MaxCharacters:N0} characters, or images must be attached.");
        if (UserImageDataUrls.Validate(images) is { } imageError) return imageError;

        // A retried send answers as the first one did, even once the run has read the steer or
        // ended: the row is already stored, so the steerability check (which asks about new
        // messages) and the user's prompt hooks do not apply a second time.
        var alreadyStored = await _steers.ExistsAsync(steerId!, ct).ConfigureAwait(false);
        if (!alreadyStored)
        {
            var run = await _store.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null) return BoundedError(404, "run_not_found", "Run not found.");
            if (!RunSteerContract.AcceptsSteer(run.State))
                return NotSteerable();

            if (_lifecycleHooks is not null)
            {
                // A run past admission is trusted; without its live root only the user's own hooks run.
                var hasRoot = _runtimeRoots.TryGetRoot(runId, out var root) && !string.IsNullOrWhiteSpace(root);
                var refusal = await _lifecycleHooks.SubmitPromptAsync(text, run.SessionId.Value,
                    hasRoot ? root! : string.Empty, workspaceTrusted: hasRoot, run.Preset, ct).ConfigureAwait(false);
                if (refusal is not null) return BoundedError(422, "prompt_blocked_by_hook", refusal);
            }
        }

        RunSteerAppendOutcome outcome;
        try
        {
            outcome = await _steers.AppendAsync(runId, steerId!, text, images, _time.UtcNow, ct).ConfigureAwait(false);
        }
        catch (HarnessStoreException error) when (error.Code == "steer_invalid")
        {
            // Lone surrogates pass the length checks above but are not valid text to store.
            return BoundedError(400, "invalid_steer_text", "text must be valid UTF-16 text.");
        }

        return outcome switch
        {
            RunSteerAppendOutcome.Accepted => Accepted(runIdRaw, steerId!, 202, RunSteerContract.StatePending),
            RunSteerAppendOutcome.Duplicate => Accepted(runIdRaw, steerId!, 200,
                await _steers.FindStateAsync(runId, steerId!, ct).ConfigureAwait(false)
                    ?? RunSteerContract.StatePending),
            RunSteerAppendOutcome.RunNotFound => BoundedError(404, "run_not_found", "Run not found."),
            RunSteerAppendOutcome.RunNotSteerable => NotSteerable(),
            RunSteerAppendOutcome.QueueFull => BoundedError(409, "steer_queue_full",
                $"At most {RunSteerContract.MaxPending} steers, {RunSteerContract.MaxPendingCharacters:N0} characters in all, can wait for one run."),
            RunSteerAppendOutcome.IdempotencyConflict => BoundedError(409, "idempotency_conflict",
                "This steer_id was already used with other text or images."),
            _ => BoundedError(500, "steer_failed", "The steer could not be stored."),
        };
    }

    public async Task<AgentServiceResult> ListAsync(string runIdRaw, CancellationToken ct)
    {
        if (!IsRunId(runIdRaw)) return BoundedError(400, "invalid_run_id", "run_id invalid.");
        var runId = new RunId(runIdRaw);
        // A child run is not the client's to read, as it is not the client's to steer.
        if (await _subagentGuard.IsInternalRunAsync(runId, ct).ConfigureAwait(false))
            return BoundedError(409, "subagent_read_only", "Subagents are controlled by the main agent. Send your request to the parent.");
        if (await _store.GetRunAsync(runId, ct).ConfigureAwait(false) is null)
            return BoundedError(404, "run_not_found", "Run not found.");
        var steers = await _steers.ListAsync(runId, ct).ConfigureAwait(false);
        return new AgentServiceResult(200, new
        {
            run_id = runIdRaw,
            steers = steers.Select(steer => new
            {
                steer_id = steer.SteerId,
                state = steer.State,
                text = steer.Text,
                created_at = steer.CreatedAt.ToString("O"),
                delivered_payload_id = RunSteerContract.DeliveredPayloadId(steer.SteerId).Value,
                image_count = steer.ImageCount,
            }),
        });
    }

    private static AgentServiceResult Accepted(string runId, string steerId, int status, string state) =>
        new(status, new
        {
            run_id = runId,
            steer_id = steerId,
            state,
            delivered_payload_id = RunSteerContract.DeliveredPayloadId(steerId).Value,
        });

    private static AgentServiceResult NotSteerable() =>
        BoundedError(409, "run_not_steerable", "This run is not working; send the message as a new turn.");
}
