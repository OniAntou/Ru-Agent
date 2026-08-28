using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RuAgent.Harness.Core;
using RuBackend.Business.Activity;
using HarnessEventId = RuAgent.Harness.Core.EventId;

namespace RuBackend.Harness;

/// <summary>
/// Projects the visible user turn into the activity log once per outbox intent.
/// The outbox payload is already the authenticated, redacted text boundary; the
/// activity store applies its own preview sanitization before persistence.
/// </summary>
public sealed class AgentConversationActivityProjector
{
    private readonly IActivityLogStore _activity;
    private readonly ILogger<AgentConversationActivityProjector>? _logger;
    private readonly ConcurrentDictionary<string, byte> _projected = new(StringComparer.Ordinal);

    public AgentConversationActivityProjector(
        IActivityLogStore activity,
        ILogger<AgentConversationActivityProjector>? logger = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _logger = logger;
    }

    public async Task ProjectUserAsync(
        HarnessEventId intentId,
        RunId runId,
        string sessionId,
        string content,
        CancellationToken cancellationToken)
    {
        _ = intentId.Value;
        _ = runId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(content);
        if (!_projected.TryAdd(intentId.Value, 0))
        {
            return;
        }

        try
        {
            await _activity.AppendAsync(
                    new ActivityEvent(
                        $"agent:{runId.Value}:{intentId.Value}:user",
                        0,
                        string.Empty,
                        "agent",
                        sessionId,
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = content,
                        }),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _projected.TryRemove(intentId.Value, out _);
            throw;
        }
        catch
        {
            // Activity is observational. A failed append must not block history or run
            // progress, and the in-process key prevents a replay storm.
            _logger?.LogWarning("agent_user_activity_projection_failed");
        }
    }
}
