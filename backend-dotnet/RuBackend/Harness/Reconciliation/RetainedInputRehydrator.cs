using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;

namespace RuBackend.Harness.Reconciliation;

/// <summary>
/// Rebuilds the proposals of a model step parked on an <c>ask_user</c> call from
/// the durable event stream. The answer route completes the asked call durably,
/// so this only has to return the proposals that were not completed; it makes
/// the WaitingUser resume path restart-safe without an in-memory stash.
/// </summary>
public sealed class RetainedInputRehydrator
{
    private readonly IHarnessEventStore _store;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly HarnessSqliteOptions _options;

    public RetainedInputRehydrator(
        IHarnessEventStore store,
        HarnessPayloadStore payloadStore,
        HarnessSqliteOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<RetainedInputStep?> RehydrateAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        if (!await HasInputRequestAsync(runId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid("retained_input_run_missing");
        if (run.State != HarnessRunState.Running)
        {
            return null;
        }

        var events = await ReadEventsAsync(runId, cancellationToken).ConfigureAwait(false);
        var pending = events
            .Where(item => string.Equals(item.Kind, "input.requested", StringComparison.Ordinal))
            .LastOrDefault();
        if (pending is null
            || pending.PayloadId is not { } requestPayloadId
            || pending.ModelStepId is not { } modelStepId
            || pending.ToolCallId is not { } askedToolCallId)
        {
            return null;
        }

        var resolved = events.Any(item =>
            item.Sequence > pending.Sequence
            && string.Equals(item.Kind, "run.state_changed", StringComparison.Ordinal)
            && string.Equals(item.SafeCode, "input_resolved", StringComparison.Ordinal));
        if (!resolved)
        {
            return null;
        }

        var inputId = await ReadInputRequestAsync(runId, requestPayloadId, cancellationToken)
            .ConfigureAwait(false);

        var proposals = await ReadProposalsAsync(runId, events, modelStepId, cancellationToken)
            .ConfigureAwait(false);
        if (!proposals.Any(item => item.ToolCallId == askedToolCallId))
        {
            throw Invalid("retained_input_ask_missing");
        }

        var completed = ReadCompletedToolCalls(events, modelStepId, proposals);
        var retained = proposals
            .Where(item => !completed.Contains(item.ToolCallId))
            .ToArray();
        return retained.Length == 0
            ? null
            : new RetainedInputStep(retained, inputId, modelStepId);
    }

    private async Task<bool> HasInputRequestAsync(RunId runId, CancellationToken cancellationToken)
    {
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM agent_events WHERE run_id = $run_id AND kind = 'input.requested' LIMIT 1";
        command.Parameters.AddWithValue("$run_id", runId.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<IReadOnlyList<ToolProposal>> ReadProposalsAsync(
        RunId runId,
        IReadOnlyList<HarnessEvent> events,
        ModelStepId modelStepId,
        CancellationToken cancellationToken)
    {
        var proposalEvents = events
            .Where(item => string.Equals(item.Kind, "tool.proposed", StringComparison.Ordinal)
                && item.ModelStepId == modelStepId)
            .OrderBy(item => item.Sequence)
            .ToArray();
        if (proposalEvents.Length == 0)
        {
            throw Invalid("retained_input_proposal_missing");
        }

        var assistantEvent = events.SingleOrDefault(item =>
            string.Equals(item.Kind, "model.step_completed", StringComparison.Ordinal)
            && item.ModelStepId == modelStepId);
        if (assistantEvent?.PayloadId is not { } assistantPayloadId)
        {
            throw Invalid("retained_input_assistant_event_invalid");
        }

        var assistant = await ReadAssistantStepAsync(runId, assistantPayloadId, cancellationToken)
            .ConfigureAwait(false);
        if (assistant.FinishReason != AssistantFinishReason.ToolCalls
            || assistant.ToolCalls.Count != proposalEvents.Length
            || assistantEvent.SafeCount != assistant.ToolCalls.Count)
        {
            throw Invalid("retained_input_assistant_calls_missing");
        }

        var proposals = new List<ToolProposal>(proposalEvents.Length);
        var toolCalls = new HashSet<ToolCallId>();
        var providers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < proposalEvents.Length; index++)
        {
            var proposalEvent = proposalEvents[index];
            var expectedOrder = index + 1;
            if (proposalEvent.SafeCount != expectedOrder)
            {
                throw Invalid("retained_input_safe_count_invalid");
            }

            var toolCallId = proposalEvent.ToolCallId
                ?? throw Invalid("retained_input_tool_call_missing");
            var providerCallId = RequireProviderCallId(proposalEvent);
            var payloadId = proposalEvent.PayloadId
                ?? throw Invalid("retained_input_payload_missing");
            if (!toolCalls.Add(toolCallId) || !providers.Add(providerCallId))
            {
                throw Invalid("retained_input_order_invalid");
            }

            var assistantCall = assistant.ToolCalls[index];
            if (!string.Equals(assistantCall.ProviderCallId, providerCallId, StringComparison.Ordinal))
            {
                throw Invalid("retained_input_provider_mismatch");
            }

            var argumentContent = await ReadPayloadAsync(runId, payloadId, cancellationToken)
                .ConfigureAwait(false);
            var arguments = ReadArguments(argumentContent, providerCallId, assistantCall);
            proposals.Add(new ToolProposal(
                runId,
                modelStepId,
                toolCallId,
                providerCallId,
                expectedOrder,
                assistantCall.ToolId,
                assistantCall.ToolVersion,
                arguments));
        }

        return proposals;
    }

    private static HashSet<ToolCallId> ReadCompletedToolCalls(
        IReadOnlyList<HarnessEvent> events,
        ModelStepId modelStepId,
        IReadOnlyList<ToolProposal> proposals)
    {
        var byToolCall = proposals.ToDictionary(item => item.ToolCallId);
        var completed = new HashSet<ToolCallId>();
        foreach (var item in events.Where(item =>
            string.Equals(item.Kind, "tool.execution_completed", StringComparison.Ordinal)
            && item.ModelStepId == modelStepId))
        {
            var toolCallId = item.ToolCallId
                ?? throw Invalid("retained_input_tool_call_missing");
            if (!byToolCall.ContainsKey(toolCallId))
            {
                throw Invalid("retained_input_execution_mismatch");
            }

            completed.Add(toolCallId);
        }

        return completed;
    }

    private async Task<InputId> ReadInputRequestAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        HarnessPayloadContent content;
        try
        {
            content = await _payloadStore
                .ReadForClientAsync(runId, payloadId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw Invalid("retained_input_request_unreadable");
        }

        if (content.Kind != HarnessPayloadKind.UserInputRequest
            || content.Encoding != HarnessPayloadEncoding.Json)
        {
            throw Invalid("retained_input_request_invalid");
        }

        try
        {
            using var document = JsonDocument.Parse(content.Plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("input_id", out var inputNode)
                || inputNode.ValueKind != JsonValueKind.String
                || inputNode.GetString() is not { } inputId
                || inputId.Length != 36
                || !inputId.StartsWith("inp_", StringComparison.Ordinal))
            {
                throw Invalid("retained_input_request_invalid");
            }

            return new InputId(inputId);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw Invalid("retained_input_request_invalid");
        }
    }

    private async Task<AssistantStep> ReadAssistantStepAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        var content = await ReadPayloadAsync(runId, payloadId, cancellationToken)
            .ConfigureAwait(false);
        if (content.Kind != HarnessPayloadKind.AssistantStep
            || content.Encoding != HarnessPayloadEncoding.Json
            || !content.Audience.HasFlag(HarnessPayloadAudience.ModelContext))
        {
            throw Invalid("retained_input_assistant_payload_invalid");
        }

        try
        {
            return AssistantStepJsonCodec.Deserialize(content.Plaintext);
        }
        catch (InvalidDataException exception) when (exception.Message.StartsWith(
            "retained_input_",
            StringComparison.Ordinal))
        {
            throw;
        }
        catch
        {
            throw Invalid("retained_input_assistant_payload_invalid");
        }
    }

    private async Task<HarnessPayloadContent> ReadPayloadAsync(
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _payloadStore
                .ReadForModelAsync(runId, payloadId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw Invalid("retained_input_payload_unreadable");
        }
    }

    private static JsonElement ReadArguments(
        HarnessPayloadContent content,
        string providerCallId,
        ModelToolCall assistantCall)
    {
        if (content.Kind != HarnessPayloadKind.ToolArguments
            || content.Encoding != HarnessPayloadEncoding.Json
            || !content.Audience.HasFlag(HarnessPayloadAudience.ModelContext))
        {
            throw Invalid("retained_input_arguments_payload_invalid");
        }

        try
        {
            using var document = JsonDocument.Parse(content.Plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadString(root, "provider_call_id", out var payloadProvider)
                || !string.Equals(payloadProvider, providerCallId, StringComparison.Ordinal)
                || !TryReadString(root, "tool_id", out var payloadToolId)
                || !string.Equals(payloadToolId, assistantCall.ToolId, StringComparison.Ordinal)
                || !TryReadString(root, "tool_version", out var payloadVersion)
                || !string.Equals(payloadVersion, assistantCall.ToolVersion, StringComparison.Ordinal)
                || !root.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object
                || !JsonNode.DeepEquals(
                    JsonNode.Parse(arguments.GetRawText()),
                    JsonNode.Parse(assistantCall.Arguments.GetRawText())))
            {
                throw Invalid("retained_input_arguments_mismatch");
            }

            return arguments.Clone();
        }
        catch (InvalidDataException exception) when (exception.Message.StartsWith(
            "retained_input_",
            StringComparison.Ordinal))
        {
            throw;
        }
        catch
        {
            throw Invalid("retained_input_arguments_invalid");
        }
    }

    private async Task<IReadOnlyList<HarnessEvent>> ReadEventsAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        var events = new List<HarnessEvent>();
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        var previousSequence = 0L;
        await foreach (var envelope in _store
            .ReadEventsAsync(runId, 0, cancellationToken)
            .ConfigureAwait(false))
        {
            if (envelope.Event.RunId != runId
                || envelope.Event.Sequence <= previousSequence
                || !eventIds.Add(envelope.Event.EventId.Value))
            {
                throw Invalid("retained_input_event_order_invalid");
            }

            previousSequence = envelope.Event.Sequence;
            events.Add(envelope.Event);
        }

        return events;
    }

    private static string RequireProviderCallId(HarnessEvent item)
    {
        var value = item.ProviderCallId;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
        {
            throw Invalid("retained_input_provider_missing");
        }

        return value;
    }

    private static bool TryReadString(
        JsonElement root,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var node)
            || node.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = node.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static InvalidDataException Invalid(string code) => new(code);
}
